using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Mvc;
using VoicebotBillingMIS.Data.Contracts;
using VoicebotBillingMIS.Data.Repositories;
using VoicebotBillingMIS.Data.Services;
using VoicebotBillingMIS.Models;

namespace VoicebotBillingMIS.Controllers;

public class UsageController : Controller
{
    private readonly IUsageService _usageService;

    public UsageController(IUsageService usageService)
    {
        _usageService = usageService;
    }

    [HttpGet]
    public async Task<ActionResult> Index(CancellationToken cancellationToken, bool clear = false)
    {
        if (clear)
        {
            Session.Remove(PageStateKeys.UsagePageState);
        }

        var storedState = Session[PageStateKeys.UsagePageState] as UsagePageState;
        var filter = storedState?.Filter ?? CreateDefaultUsageFilter();

        if (clear)
        {
            filter = CreateDefaultUsageFilter();
        }
        else if (storedState != null)
        {
            filter.DepartmentIds ??= new List<int>();
            filter.CampaignIds ??= new List<int>();
            filter.VendorIds ??= new List<int>();
        }

        await ApplyCurrentVendorRatesAsync(filter, cancellationToken);
        Session[PageStateKeys.UsagePageState] = new UsagePageState { Filter = filter };

        // The initial page load only prepares the filters. Fetch usage data
        // after the user submits a filter so login is not blocked by SQL.
        var model = await BuildViewModelAsync(filter, cancellationToken, includeUsageRecords: false);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> Index(UsageFilterInputModel filter, CancellationToken cancellationToken)
    {
        filter ??= new UsageFilterInputModel();
        filter.DepartmentIds ??= new List<int>();
        filter.CampaignIds ??= new List<int>();
        filter.VendorIds ??= new List<int>();

        await ApplyCurrentVendorRatesAsync(filter, cancellationToken);
        Session[PageStateKeys.UsagePageState] = new UsagePageState { Filter = filter };

        var hasValidDateRange = filter.From.HasValue && filter.To.HasValue;
        var hasCampaignSelection = filter.CampaignIds.Count > 0;
        UsagePageViewModel model;
        try
        {
            model = await BuildViewModelAsync(
                filter,
                cancellationToken,
                includeUsageRecords: hasValidDateRange && hasCampaignSelection);
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            model = await BuildViewModelAsync(
                filter,
                cancellationToken,
                includeUsageRecords: false);
            model.ErrorMessage =
                "Usage data could not be fetched within the database timeout. Try a smaller date range or check the stored procedure on the database server.";
        }
        catch (TimeoutException)
        {
            model = await BuildViewModelAsync(
                filter,
                cancellationToken,
                includeUsageRecords: false);
            model.ErrorMessage =
                "Usage data could not be fetched within the request timeout. Try a smaller date range or check the stored procedure on the database server.";
        }

        if (!hasValidDateRange)
        {
            model.ErrorMessage = "Both From and To date must be specified";
        }
        else if (!hasCampaignSelection)
        {
            model.ErrorMessage = "No campaign selected";
        }
        // Campaign-only filtering (Department x, Vendor x, Campaign ✓) is now a valid standalone filter state.

        return View(model); // Full-page POST pattern keeps interaction simple and consistent.
    }

    [HttpGet]
    public async Task<ActionResult> Export(CancellationToken cancellationToken)
    {
        var state = Session[PageStateKeys.UsagePageState] as UsagePageState;
        var filter = state?.Filter ?? CreateDefaultUsageFilter();
        var records = await _usageService.GetUsageRecordsAsync(
            new UsageFilterRequest
            {
                DepartmentIds = new List<int>(),
                CampaignIds = filter.CampaignIds ?? new List<int>(),
                VendorIds = new List<int>(),
                From = filter.From,
                To = filter.To,
                GreylabsRatePerMinute = filter.GreylabsRatePerMinute,
                FogteamsRatePerMinute = filter.FogteamsRatePerMinute,
                GnaniRatePerMinute = filter.GnaniRatePerMinute
            },
            cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("Dept,Campaign,Vendor,Unique Base,Total Attempts,Attempt Intensity,Unique Connects,Total Connects,Connect Intensity,Billable Minutes (MOU),Running Cost (₹ incl. GST),Monthly Budget (₹),Applicable Time Budget (₹),Budget Burn (%),Expected Time Coverage (%),Pace (%),Budget Burn Status");

        foreach (var row in records)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                CsvValue(row.Department),
                CsvValue(row.Campaign),
                CsvValue(row.Vendor),
                CsvValue(row.UniqueBase),
                CsvValue(row.TotalAttempts),
                CsvValue(row.AttemptIntensity),
                CsvValue(row.UniqueConnects),
                CsvValue(row.TotalConnects),
                CsvValue(row.ConnectIntensity),
                CsvValue(row.Mou),
                CsvValue(row.RunningCost),
                CsvValue(row.BudgetAmount.ToString("F2", CultureInfo.InvariantCulture)),
                CsvValue(row.ApplicableTimeBudget?.ToString("F2", CultureInfo.InvariantCulture)),
                CsvValue(row.BudgetBurnPercentage?.ToString("F2", CultureInfo.InvariantCulture)),
                CsvValue(row.ExpectedTimeCoveragePercentage?.ToString("F2", CultureInfo.InvariantCulture)),
                CsvValue(row.PaceRatio.HasValue
                    ? (row.PaceRatio.Value * 100m).ToString("F2", CultureInfo.InvariantCulture)
                    : null),
                CsvValue(row.BudgetStatus.ToString())
            }));
        }

        var from = FormatExportDate(filter.From);
        var to = FormatExportDate(filter.To);
        var fileName = $"Voicebot-Bill-Data-{from}-to-{to}.csv";
        var csvContent = Encoding.UTF8.GetBytes(csv.ToString());
        var utf8Bom = Encoding.UTF8.GetPreamble();
        var downloadContent = new byte[utf8Bom.Length + csvContent.Length];
        Buffer.BlockCopy(utf8Bom, 0, downloadContent, 0, utf8Bom.Length);
        Buffer.BlockCopy(csvContent, 0, downloadContent, utf8Bom.Length, csvContent.Length);
        return File(downloadContent, "text/csv", fileName);
    }

    private static string CsvValue<T>(T? value) where T : struct
    {
        return value.HasValue
            ? CsvValue(Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty)
            : string.Empty;
    }

    private static string CsvValue(string? value)
    {
        var text = value ?? string.Empty;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static string FormatExportDate(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToString("MM-dd-yyyy-h-mmtt", CultureInfo.InvariantCulture)
            : "Not-Set";
    }

    private static UsageFilterInputModel CreateDefaultUsageFilter()
    {
        var now = DateTime.Now;
        return new UsageFilterInputModel
        {
            From = new DateTime(now.Year, now.Month, 1),
            To = now,
            DepartmentIds = new List<int>(),
            CampaignIds = new List<int>(),
            VendorIds = new List<int>()
        };
    }

    private async Task ApplyCurrentVendorRatesAsync(
        UsageFilterInputModel filter,
        CancellationToken cancellationToken)
    {
        var vendors = await _usageService.GetVendorsAsync(cancellationToken);
        filter.GreylabsRatePerMinute = vendors
            .Where(vendor => string.Equals(vendor.Name, "Greylabs", StringComparison.OrdinalIgnoreCase))
            .Select(vendor => UsageMasterData.GetCurrentVendorRate(vendor.Id))
            .First();
        filter.FogteamsRatePerMinute = vendors
            .Where(vendor => string.Equals(vendor.Name, "Fogteams", StringComparison.OrdinalIgnoreCase))
            .Select(vendor => UsageMasterData.GetCurrentVendorRate(vendor.Id))
            .First();
        filter.GnaniRatePerMinute = vendors
            .Where(vendor => string.Equals(vendor.Name, "Gnani", StringComparison.OrdinalIgnoreCase))
            .Select(vendor => UsageMasterData.GetCurrentVendorRate(vendor.Id))
            .First();
    }

    private async Task<UsagePageViewModel> BuildViewModelAsync(
        UsageFilterInputModel filter,
        CancellationToken cancellationToken,
        bool includeUsageRecords = true)
    {
        var departmentsTask = _usageService.GetDepartmentsAsync(cancellationToken);
        var campaignsTask = _usageService.GetCampaignsAsync(cancellationToken);
        var vendorsTask = _usageService.GetVendorsAsync(cancellationToken);

        var repositoryFilter = new UsageFilterRequest
        {
            DepartmentIds = new List<int>(),
            CampaignIds = filter.CampaignIds,
            VendorIds = new List<int>(),
            From = filter.From,
            To = filter.To,
            GreylabsRatePerMinute = filter.GreylabsRatePerMinute,
            FogteamsRatePerMinute = filter.FogteamsRatePerMinute,
            GnaniRatePerMinute = filter.GnaniRatePerMinute
        };

        Task<IReadOnlyList<VoicebotBillingMIS.Data.Models.UsageRecordViewModel>> usageTask = includeUsageRecords
            ? _usageService.GetUsageRecordsAsync(repositoryFilter, cancellationToken)
            : Task.FromResult<IReadOnlyList<VoicebotBillingMIS.Data.Models.UsageRecordViewModel>>(
                new List<VoicebotBillingMIS.Data.Models.UsageRecordViewModel>());

        await Task.WhenAll(departmentsTask, campaignsTask, vendorsTask, usageTask);

        return new UsagePageViewModel
        {
            Filter = filter,
            Departments = departmentsTask.Result,
            Campaigns = campaignsTask.Result,
            Vendors = vendorsTask.Result,
            UsageRecords = usageTask.Result
        };
    }
}
