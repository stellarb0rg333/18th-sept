using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Mvc;
using VoicebotBillingMIS.Data.Contracts;
using VoicebotBillingMIS.Data.Models;
using VoicebotBillingMIS.Data.Repositories;
using VoicebotBillingMIS.Data.Services;
using VoicebotBillingMIS.Infrastructure;
using VoicebotBillingMIS.Models;

namespace VoicebotBillingMIS.Controllers;

public class InvoiceController : Controller
{
    private readonly IInvoiceHistoryRepository _invoiceHistoryRepository;
    private readonly IUsageService _usageService;
    private readonly IInvoicePdfParserFactory _invoicePdfParserFactory;

    public InvoiceController(
        IInvoiceHistoryRepository invoiceHistoryRepository,
        IUsageService usageService,
        IInvoicePdfParserFactory invoicePdfParserFactory)
    {
        _invoiceHistoryRepository = invoiceHistoryRepository;
        _usageService = usageService;
        _invoicePdfParserFactory = invoicePdfParserFactory;
    }

    [HttpGet]
    public async Task<ActionResult> Upload(CancellationToken cancellationToken, bool clear = false)
    {
        if (clear)
        {
            Session.Remove(PageStateKeys.InvoiceUploadPageState);
        }

        var storedState = Session[PageStateKeys.InvoiceUploadPageState] as InvoiceUploadPageState;
        var input = storedState?.Input ?? new InvoiceUploadInputModel();
        var rows = storedState?.Rows ?? [];
        if (clear)
        {
            input = new InvoiceUploadInputModel();
            rows = [];
        }
        await ApplyVendorRateDefaultsAsync(input, cancellationToken);

        Session[PageStateKeys.InvoiceUploadPageState] =
            new InvoiceUploadPageState { Input = input, Rows = rows };
        return View(await BuildUploadViewModelAsync(input, rows, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> Upload(InvoiceUploadInputModel input, CancellationToken cancellationToken)
    {
        input ??= new InvoiceUploadInputModel();
        await ApplyVendorRateDefaultsAsync(input, cancellationToken);
        var pageState = new InvoiceUploadPageState
        {
            Input = input,
            Rows = new List<InvoiceUploadRow>()
        };
        Session[PageStateKeys.InvoiceUploadPageState] = pageState;

        var model = await BuildUploadViewModelAsync(input, pageState.Rows, cancellationToken);
        if (!TryValidateUploadInput(input, model))
        {
            return View(model);
        }

        try
        {
            var uploadedPdf = input.InvoicePdf;
            if (uploadedPdf == null)
            {
                throw new InvalidDataException("The uploaded PDF content is unavailable.");
            }

            input.OriginalFileName = Path.GetFileName(uploadedPdf.FileName);
            var parser = _invoicePdfParserFactory.Create(input.VendorName!);
            using var uploadStream = uploadedPdf.InputStream;
            using var pdfBuffer = new MemoryStream();
            uploadStream.CopyTo(pdfBuffer);
            var pdfBytes = pdfBuffer.ToArray();
            input.PdfPreviewContent = pdfBytes;
            using var parserStream = new MemoryStream(pdfBytes);
            var parsedLines = parser.Parse(parserStream);
            input.InvoicePdf = null;
            var (defaultFromDate, defaultToDate) = GetPreviousMonthDateRange(DateTime.Now);
            pageState.Rows = parsedLines.Select(line => new InvoiceUploadRow
            {
                BillCampaign = line.BillCampaign,
                BillMou = line.BillMou,
                FromDate = defaultFromDate,
                ToDate = defaultToDate,
                VendorName = input.VendorName!
            }).ToList();

            model = await BuildUploadViewModelAsync(input, pageState.Rows, cancellationToken);
            model.Message = parsedLines.Count == 0
                ? "The selected vendor parser found no invoice rows."
                : $"Extracted {parsedLines.Count} invoice row(s).";
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is InvalidOperationException || ex is ArgumentException)
        {
            input.InvoicePdf = null;
            model.ErrorMessage = "The invoice PDF could not be parsed. Verify that it matches the selected vendor.";
        }

        Session[PageStateKeys.InvoiceUploadPageState] = pageState;
        return View(model);
    }

    [HttpGet]
    [Route("Invoice/PreviewPdf", Name = "InvoicePreviewPdf")]
    public ActionResult PreviewPdf()
    {
        var state = Session[PageStateKeys.InvoiceUploadPageState] as InvoiceUploadPageState;
        var content = state?.Input?.PdfPreviewContent;
        if (content == null || content.Length == 0)
        {
            return HttpNotFound();
        }

        return File(content, "application/pdf");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> FetchRow(
        int rowIndex,
        int? internalCampaignId,
        DateTime? fromDate,
        DateTime? toDate,
        CancellationToken cancellationToken)
    {
        var state = Session[PageStateKeys.InvoiceUploadPageState] as InvoiceUploadPageState;
        var rows = state?.Rows ?? [];
        if (state is null || rowIndex < 0 || rowIndex >= rows.Count)
        {
            return HttpNotFound();
        }

        var input = state.Input ?? new InvoiceUploadInputModel();
        var row = rows[rowIndex];
        row.InternalCampaignId = internalCampaignId;
        row.FromDate = fromDate;
        row.ToDate = toDate;
        row.ErrorMessage = null;

        var campaigns = await _usageService.GetCampaignsAsync(cancellationToken);
        if (!internalCampaignId.HasValue ||
            campaigns.All(campaign => campaign.Id != internalCampaignId.Value))
        {
            row.ErrorMessage = "Please select a valid internal campaign.";
            return await RenderFetchResultAsync(input, rows, cancellationToken);
        }

        if (!fromDate.HasValue)
        {
            row.ErrorMessage = "Please choose a From Date.";
            return await RenderFetchResultAsync(input, rows, cancellationToken);
        }

        if (!toDate.HasValue)
        {
            row.ErrorMessage = "Please choose a To Date.";
            return await RenderFetchResultAsync(input, rows, cancellationToken);
        }

        if (fromDate.Value > toDate.Value)
        {
            row.ErrorMessage = "From Date must not be after To Date.";
            return await RenderFetchResultAsync(input, rows, cancellationToken);
        }

        row.VendorName = input.VendorName ?? row.VendorName;
        row.VendorRate = ResolveVendorRate(input, row.VendorName);
        if (row.VendorRate <= 0m)
        {
            row.ErrorMessage = "The selected vendor rate is missing or invalid.";
            return await RenderFetchResultAsync(input, rows, cancellationToken);
        }

        try
        {
            var usageRecords = await _usageService.GetUsageRecordsAsync(
                new UsageFilterRequest
                {
                    CampaignIds = new List<int> { internalCampaignId.Value },
                    From = fromDate.Value.Date,
                    To = toDate.Value.Date,
                    GreylabsRatePerMinute = input.GreylabsRatePerMinute ?? 0m,
                    FogteamsRatePerMinute = input.FogteamsRatePerMinute ?? 0m,
                    GnaniRatePerMinute = input.GnaniRatePerMinute ?? 0m
                },
                cancellationToken);

            if (usageRecords.Count == 0 || !usageRecords.Any(record => record.Mou.HasValue))
            {
                row.TrackedMou = null;
                row.CostDifference = null;
                row.ErrorMessage = "Tracked MOU was not available for the selected campaign and date range.";
            }
            else
            {
                row.TrackedMou = usageRecords
                    .Where(record => record.Mou.HasValue)
                    .Sum(record => record.Mou!.Value);
                row.CostDifference = row.CalculateCostDifference();
            }
        }
        catch (Exception)
        {
            row.TrackedMou = null;
            row.CostDifference = null;
            row.ErrorMessage = "Tracked MOU could not be retrieved for this row.";
        }

        return await RenderFetchResultAsync(input, rows, cancellationToken);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> SaveInvoice(CancellationToken cancellationToken)
    {
        var state = Session[PageStateKeys.InvoiceUploadPageState] as InvoiceUploadPageState;
        if (state is null || state.SaveCompleted)
        {
            return RedirectToAction(nameof(Upload), new { clear = true });
        }

        var input = state.Input ?? new InvoiceUploadInputModel();
        var rows = state.Rows ?? [];
        var campaigns = await _usageService.GetCampaignsAsync(cancellationToken);
        var validationErrors = ValidateInvoiceForSave(input, rows, campaigns);
        if (validationErrors.Count > 0)
        {
            var invalidModel = await BuildUploadViewModelAsync(input, rows, cancellationToken);
            invalidModel.ErrorMessage = string.Join(" ", validationErrors);
            return View("Upload", invalidModel);
        }

        var selectedCampaigns = campaigns.ToDictionary(c => c.Id);
        var invoiceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var snapshotRows = rows.Select((row, index) => new InvoiceReconciliationRow
        {
            // ValidateInvoiceForSave has already required this value.
            RowIndex = index,
            BillCampaignName = row.BillCampaign,
            BillMou = row.BillMou,
            InternalCampaignId = row.InternalCampaignId,
            InternalCampaignName = selectedCampaigns[row.InternalCampaignId.GetValueOrDefault()].Name,
            FromDate = row.FromDate,
            ToDate = row.ToDate,
            TrackedMou = row.TrackedMou,
            CostDifference = row.CostDifference
        }).ToList();

        var history = new InvoiceHistoryRecord
        {
            Id = invoiceId,
            ComparedUtc = now,
            VendorName = input.VendorName!,
            CreatedBy = User?.Identity?.IsAuthenticated == true
                ? User.Identity.Name ?? string.Empty
                : string.Empty,
            RowCount = snapshotRows.Count,
            Comparison = new InvoiceComparisonContext
            {
                VendorRate = ResolveVendorRate(input, input.VendorName!),
                ReconciliationRows = snapshotRows,
                Record = new InvoiceRecord
                {
                    Id = invoiceId,
                    OriginalFileName = input.OriginalFileName,
                    StoredPdfFileName = invoiceId.ToString("N") + ".pdf",
                    UploadedUtc = now,
                    ExtractionStatus = InvoiceExtractionStatus.ExtractedConfirmed,
                    ExtractionMessage = $"Saved {snapshotRows.Count} invoice row(s)."
                }
            }
        };

        try
        {
            if (await _invoiceHistoryRepository.IsDuplicateAsync(history, cancellationToken))
            {
                var duplicateModel = await BuildUploadViewModelAsync(input, rows, cancellationToken);
                duplicateModel.ErrorMessage =
                    "This vendor, PDF, and invoice analysis has already been saved. Delete the previous analysis from Invoice History before saving it again.";
                duplicateModel.Message =
                    "Duplicate invoice analysis was not saved.";
                return View("Upload", duplicateModel);
            }

            var invoiceFilesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "InvoiceFiles");
            Directory.CreateDirectory(invoiceFilesDirectory);
            if (input.PdfPreviewContent == null || input.PdfPreviewContent.Length == 0)
            {
                throw new IOException("The uploaded PDF content is unavailable.");
            }

            System.IO.File.WriteAllBytes(
                Path.Combine(invoiceFilesDirectory, history.Comparison.Record.StoredPdfFileName),
                input.PdfPreviewContent);
            await _invoiceHistoryRepository.SaveAsync(history, cancellationToken);
        }
        catch (IOException)
        {
            var failedModel = await BuildUploadViewModelAsync(input, rows, cancellationToken);
            failedModel.ErrorMessage = "The invoice could not be saved. Your completed upload is still available to retry.";
            return View("Upload", failedModel);
        }

        state.SaveCompleted = true;
        Session.Remove(PageStateKeys.InvoiceUploadPageState);
        TempData["InvoiceHistoryMessage"] = $"Invoice {invoiceId} saved successfully.";
        return RedirectToAction(nameof(History), new { id = invoiceId });
    }

    [HttpGet]
    public async Task<ActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var history = await _invoiceHistoryRepository.GetByIdAsync(id, cancellationToken);
        if (history?.Comparison is null)
        {
            return HttpNotFound();
        }

        return View(history.Comparison);
    }

    [HttpGet]
    public async Task<ActionResult> HistoryPdf(Guid id, CancellationToken cancellationToken)
    {
        var history = await _invoiceHistoryRepository.GetByIdAsync(id, cancellationToken);
        var storedFileName = history?.Comparison?.Record?.StoredPdfFileName;
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var candidatePaths = new[]
        {
            string.IsNullOrWhiteSpace(storedFileName)
                ? null
                : Path.Combine(baseDirectory, "App_Data", "InvoiceFiles", Path.GetFileName(storedFileName)),
            Path.Combine(baseDirectory, "App_Data", "InvoiceFiles", id.ToString("N") + ".pdf"),
            Path.Combine(baseDirectory, "App_Data", "Invoices", id.ToString("N") + ".pdf")
        };
        var path = candidatePaths.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && System.IO.File.Exists(candidate));
        if (path is null)
        {
            return HttpNotFound();
        }

        return File(path, "application/pdf");
    }

    [HttpGet]
    public async Task<ActionResult> History(Guid? id, CancellationToken cancellationToken)
    {
        var records = await _invoiceHistoryRepository.GetAllAsync(cancellationToken);
        InvoiceHistoryRecord? selected = null;
        string? message = null;

        if (id.HasValue)
        {
            Session[PageStateKeys.InvoiceHistoryPageState] =
                new InvoiceHistoryPageState { SelectedRecordId = id.Value };
        }
        else if (Session[PageStateKeys.InvoiceHistoryPageState] is InvoiceHistoryPageState state)
        {
            id = state.SelectedRecordId;
        }

        if (id.HasValue)
        {
            selected = await _invoiceHistoryRepository.GetByIdAsync(id.Value, cancellationToken);
            if (selected is null)
            {
                message = "The selected invoice history record could not be found.";
            }
        }

        return View(new InvoiceHistoryPageViewModel
        {
            Records = records,
            SelectedRecord = selected,
            Message = message ?? TempData["InvoiceHistoryMessage"] as string
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> DeleteHistory(Guid id, CancellationToken cancellationToken)
    {
        await _invoiceHistoryRepository.DeleteAsync(id, cancellationToken);
        Session.Remove(PageStateKeys.InvoiceHistoryPageState);
        return RedirectToAction(nameof(History));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> RemoveRow(int rowIndex, CancellationToken cancellationToken)
    {
        var state = Session[PageStateKeys.InvoiceUploadPageState] as InvoiceUploadPageState;
        if (state == null || rowIndex < 0 || rowIndex >= state.Rows.Count)
        {
            return HttpNotFound();
        }

        state.Rows.RemoveAt(rowIndex);
        Session[PageStateKeys.InvoiceUploadPageState] = state;
        return View("Upload", await BuildUploadViewModelAsync(state.Input, state.Rows, cancellationToken));
    }

    [HttpGet]
    public async Task<ActionResult> MonthlyHistory(int? year, int? month, CancellationToken cancellationToken)
    {
        var records = await _invoiceHistoryRepository.GetAllAsync(cancellationToken);
        var availableYears = records.SelectMany(record => record.Comparison?.ReconciliationRows ?? Array.Empty<InvoiceReconciliationRow>())
            .Select(row => row.FromDate)
            .Where(date => date.HasValue)
            .Select(date => date.Value.Year)
            .Distinct()
            .OrderByDescending(value => value)
            .ToList();
        var selectedYear = year ?? (availableYears.Count > 0 ? availableYears[0] : DateTime.Now.Year);
        var selectedMonth = month.HasValue && month.Value >= 1 && month.Value <= 12 ? month.Value : DateTime.Now.Month;
        if (!availableYears.Contains(selectedYear))
        {
            availableYears.Add(selectedYear);
            availableYears.Sort();
            availableYears.Reverse();
        }
        var rows = BuildMonthlyRows(records, selectedYear, selectedMonth);

        return View(new MonthlyHistoryViewModel
        {
            Year = selectedYear,
            Month = selectedMonth,
            Years = availableYears,
            Rows = rows,
            TotalBillCost = rows.Sum(row => row.BillCost),
            TotalTrackedCost = rows.Where(row => row.TrackedCost.HasValue).Sum(row => row.TrackedCost ?? 0m),
            Message = TempData["InvoiceMonthlyMessage"] as string ?? string.Empty
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult> DeleteMonthlyHistory(int year, int month, CancellationToken cancellationToken)
    {
        if (year < 1 || month < 1 || month > 12)
        {
            return RedirectToAction(nameof(MonthlyHistory));
        }

        var count = await _invoiceHistoryRepository.DeleteRowsForMonthAsync(year, month, cancellationToken);
        TempData["InvoiceMonthlyMessage"] = count == 0
            ? "No reconciliation rows matched the selected month."
            : $"Deleted {count} reconciliation row(s) for {new DateTime(year, month, 1):MMMM yyyy}.";
        return RedirectToAction(nameof(MonthlyHistory), new { year = year, month = month });
    }

    private static List<MonthlyHistoryRowViewModel> BuildMonthlyRows(
        IReadOnlyList<InvoiceHistoryRecord> records,
        int year,
        int month)
    {
        var rows = new List<MonthlyHistoryRowViewModel>();
        foreach (var record in records.OrderByDescending(item => item.ComparedUtc).ThenBy(item => item.Id))
        {
            var rate = record.Comparison?.VendorRate ?? 0m;
            foreach (var row in record.Comparison?.ReconciliationRows ?? Array.Empty<InvoiceReconciliationRow>())
            {
                if (!row.FromDate.HasValue || row.FromDate.Value.Year != year || row.FromDate.Value.Month != month)
                {
                    continue;
                }

                rows.Add(new MonthlyHistoryRowViewModel
                {
                    FromDate = row.FromDate,
                    ToDate = row.ToDate,
                    Vendor = record.VendorName,
                    Rate = rate,
                    BillCampaign = row.BillCampaignName,
                    BillMou = row.BillMou ?? 0m,
                    TrackedMou = row.TrackedMou
                });
            }
        }

        return rows
            .GroupBy(row => string.Join("|",
                row.FromDate?.ToString("O") ?? string.Empty,
                row.ToDate?.ToString("O") ?? string.Empty,
                row.Vendor,
                row.Rate.ToString("G29"),
                row.BillCampaign,
                row.BillMou.ToString("G29")), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(row => row.FromDate)
            .ThenBy(row => row.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.BillCampaign, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<ActionResult> RenderFetchResultAsync(
        InvoiceUploadInputModel input,
        List<InvoiceUploadRow> rows,
        CancellationToken cancellationToken)
    {
        Session[PageStateKeys.InvoiceUploadPageState] = new InvoiceUploadPageState
        {
            Input = input,
            Rows = rows
        };
        return View("Upload", await BuildUploadViewModelAsync(input, rows, cancellationToken));
    }

    private async Task<InvoiceUploadPageViewModel> BuildUploadViewModelAsync(
        InvoiceUploadInputModel input,
        IReadOnlyCollection<InvoiceUploadRow> rows,
        CancellationToken cancellationToken)
    {
        var campaigns = await _usageService.GetCampaignsAsync(cancellationToken);
        var vendorId = ResolveVendorId(input.VendorName);
        if (vendorId.HasValue)
        {
            campaigns = campaigns
                .Where(campaign => campaign.VendorId == vendorId.Value)
                .ToList();
        }
        else
        {
            campaigns = [];
        }

        var normalizedRows = rows.Select(row =>
        {
            if (string.IsNullOrWhiteSpace(row.VendorName))
            {
                row.VendorName = input.VendorName ?? string.Empty;
            }

            row.VendorRate = ResolveVendorRate(input, row.VendorName);
            row.CostDifference = row.CalculateCostDifference();
            return row;
        }).ToList();

        return new InvoiceUploadPageViewModel
        {
            Input = input,
            Campaigns = campaigns,
            Rows = normalizedRows
        };
    }

    private static List<string> ValidateInvoiceForSave(
        InvoiceUploadInputModel input,
        IReadOnlyList<InvoiceUploadRow> rows,
        IReadOnlyList<Campaign> campaigns)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.VendorName) ||
            !new[] { "Fogteams", "Greylabs", "Gnani" }
                .Contains(input.VendorName, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("A valid vendor is required.");
        }

        if (ResolveVendorRate(input, input.VendorName ?? string.Empty) <= 0m)
        {
            errors.Add("The selected vendor rate must be greater than zero.");
        }

        if (rows.Count == 0)
        {
            errors.Add("At least one parsed invoice row is required.");
        }

        var campaignIds = new HashSet<int>(campaigns.Select(campaign => campaign.Id));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (string.IsNullOrWhiteSpace(row.BillCampaign) || row.BillMou < 0m)
            {
                errors.Add($"Row {index + 1} has invalid bill data.");
            }
            if (!row.InternalCampaignId.HasValue || !campaignIds.Contains(row.InternalCampaignId.Value))
            {
                errors.Add($"Row {index + 1} requires a valid Campaign selection.");
            }
            if (!row.FromDate.HasValue || !row.ToDate.HasValue)
            {
                errors.Add($"Row {index + 1} requires both dates.");
            }
            else
            {
                var rowFromDate = row.FromDate.Value;
                var rowToDate = row.ToDate.Value;
                if (rowFromDate > rowToDate)
                {
                    errors.Add($"Row {index + 1} has From Date after To Date.");
                }
            }
            if (!row.TrackedMou.HasValue || !row.CostDifference.HasValue)
            {
                errors.Add($"Row {index + 1} must be fetched successfully before saving.");
            }
        }

        return errors;
    }

    private static bool TryValidateUploadInput(InvoiceUploadInputModel input, InvoiceUploadPageViewModel model)
    {
        model.ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(input.VendorName))
        {
            model.ErrorMessage = "Please select a vendor.";
            return false;
        }

        if (!new[] { "Fogteams", "Greylabs", "Gnani" }
            .Contains(input.VendorName, StringComparer.OrdinalIgnoreCase))
        {
            model.ErrorMessage = "Vendor selection is invalid.";
            return false;
        }

        if (!input.FogteamsRatePerMinute.HasValue || input.FogteamsRatePerMinute.Value <= 0m ||
            !input.GreylabsRatePerMinute.HasValue || input.GreylabsRatePerMinute.Value <= 0m ||
            !input.GnaniRatePerMinute.HasValue || input.GnaniRatePerMinute.Value <= 0m)
        {
            model.ErrorMessage = "All vendor ₹/min rates must be positive numbers.";
            return false;
        }

        if (input.InvoicePdf == null || input.InvoicePdf.ContentLength <= 0)
        {
            model.ErrorMessage = "Please upload a PDF invoice.";
            return false;
        }

        var extension = Path.GetExtension(input.InvoicePdf.FileName);
        if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(input.InvoicePdf.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            model.ErrorMessage = "Only PDF files are supported.";
            return false;
        }

        return true;
    }

    private static decimal ResolveVendorRate(InvoiceUploadInputModel input, string vendorName)
    {
        if (string.Equals(vendorName, "Fogteams", StringComparison.OrdinalIgnoreCase))
        {
            return input.FogteamsRatePerMinute ?? 0m;
        }
        if (string.Equals(vendorName, "Greylabs", StringComparison.OrdinalIgnoreCase))
        {
            return input.GreylabsRatePerMinute ?? 0m;
        }
        if (string.Equals(vendorName, "Gnani", StringComparison.OrdinalIgnoreCase))
        {
            return input.GnaniRatePerMinute ?? 0m;
        }
        return 0m;
    }

    private static int? ResolveVendorId(string? vendorName)
    {
        var vendor = UsageMasterData.Vendors
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, vendorName, StringComparison.OrdinalIgnoreCase));
        return vendor?.Id;
    }

    private static (DateTime From, DateTime To) GetPreviousMonthDateRange(DateTime now)
    {
        var currentMonthStart = new DateTime(now.Year, now.Month, 1);
        var previousMonthEnd = currentMonthStart.AddTicks(-1);
        var previousMonthStart = new DateTime(
            previousMonthEnd.Year,
            previousMonthEnd.Month,
            1);
        return (previousMonthStart, previousMonthEnd);
    }

    private async Task ApplyVendorRateDefaultsAsync(
        InvoiceUploadInputModel input,
        CancellationToken cancellationToken)
    {
        var vendors = await _usageService.GetVendorsAsync(cancellationToken);
        input.FogteamsRatePerMinute = vendors
            .First(vendor => string.Equals(vendor.Name, "Fogteams", StringComparison.OrdinalIgnoreCase))
            .PerMinuteRate.HasValue
                ? UsageMasterData.GetCurrentVendorRate(vendors.First(vendor => string.Equals(vendor.Name, "Fogteams", StringComparison.OrdinalIgnoreCase)).Id)
                : null;
        input.GreylabsRatePerMinute = vendors
            .First(vendor => string.Equals(vendor.Name, "Greylabs", StringComparison.OrdinalIgnoreCase))
            .PerMinuteRate.HasValue
                ? UsageMasterData.GetCurrentVendorRate(vendors.First(vendor => string.Equals(vendor.Name, "Greylabs", StringComparison.OrdinalIgnoreCase)).Id)
                : null;
        input.GnaniRatePerMinute = vendors
            .First(vendor => string.Equals(vendor.Name, "Gnani", StringComparison.OrdinalIgnoreCase))
            .PerMinuteRate.HasValue
                ? UsageMasterData.GetCurrentVendorRate(vendors.First(vendor => string.Equals(vendor.Name, "Gnani", StringComparison.OrdinalIgnoreCase)).Id)
                : null;
    }
}
