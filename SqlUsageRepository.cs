using System.Collections.Concurrent;
using System.Data;
using System.Data.SqlClient;
using VoicebotBillingMIS.Data.Contracts;
using VoicebotBillingMIS.Data.Models;

namespace VoicebotBillingMIS.Data.Repositories;

/// <summary>
/// SQL Server integration for funnel usage using dbo.sp_vb_cost_analysis_dg.
/// Returns the metrics supplied by dbo.sp_vb_cost_analysis_dg. The procedure
/// supplies the final MOU value in its duration column; cost is calculated
/// later from MOU, vendor rate, and GST.
///
/// Known unmapped Call Flow IDs (to be surfaced explicitly, never dropped silently):
/// - 2c8dc98d-18bd-4197-9280-9648177dc79b
/// - 4f2c30f7-3097-4aff-9a21-65d3a822fa6e
/// 
/// These IDs have no campaign mapping yet but represent real, billable usage and must 
/// appear in the results so the total cost shown on the dashboard never hides unmapped data.
/// </summary>
public class SqlUsageRepository : IUsageRepository
{
    private static readonly ConcurrentDictionary<string, RawUsageCacheEntry> RawUsageCache = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RawUsageFetchLocks = new();
    private static readonly TimeSpan RawUsageCacheLifetime = TimeSpan.FromMinutes(10);

    private readonly string _connectionString;

    // Call Flow ID -> Campaign name mapping (seeded from Campaign.CallFlowId in database)
    private readonly Dictionary<string, Campaign> _callFlowIdMapping;
    private readonly IReadOnlyList<Campaign> _campaigns;
    private readonly IReadOnlyList<Vendor> _vendors;
    private readonly IReadOnlyList<Department> _departments;

    public static void ClearUsageCache()
    {
        RawUsageCache.Clear();
    }

    public SqlUsageRepository(string connectionString, IReadOnlyList<Campaign> campaigns, 
        IReadOnlyList<Vendor> vendors, IReadOnlyList<Department> departments)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _campaigns = campaigns;
        _vendors = vendors;
        _departments = departments;

        // Build lookup: CallFlowId -> Campaign
        _callFlowIdMapping = campaigns
            .Where(c => !string.IsNullOrWhiteSpace(c.CallFlowId))
            .ToDictionary(c => c.CallFlowId!, c => c, StringComparer.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<Department>> GetDepartmentsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_departments);

    public Task<IReadOnlyList<Campaign>> GetCampaignsAsync(CancellationToken cancellationToken = default)
    {
        UsageMasterData.RefreshSharedState();
        return Task.FromResult((IReadOnlyList<Campaign>)_campaigns
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    public Task<IReadOnlyList<Vendor>> GetVendorsAsync(CancellationToken cancellationToken = default)
    {
        UsageMasterData.RefreshSharedState();
        return Task.FromResult(_vendors);
    }

    public Task<IReadOnlyList<VendorRateHistory>> GetVendorRateHistoryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(UsageMasterData.GetVendorRateHistory());

    public Task<VendorRateHistory> AddVendorRateAsync(int vendorId, decimal ratePerMinute, DateTime effectiveFrom, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            UsageMasterData.AddVendorRate(vendorId, ratePerMinute, effectiveFrom));
    }

    public Task<bool> UpdateCampaignBudgetAsync(int campaignId, decimal budgetAmount, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(UsageMasterData.UpdateCampaignBudget(campaignId, budgetAmount));
    }

    public async Task<IReadOnlyList<UsageRecord>> GetUsageRecordsAsync(UsageFilterRequest filterRequest, CancellationToken cancellationToken = default)
    {
        var result = new List<UsageRecord>();
        var unmappedRows = new List<(string CampaignName, string CallFlowId, int? TotalAttempts,
            int? UniqueBase, int? TotalConnects, int? UniqueConnects, long? Mou)>();

        var cacheKey = CreateRawUsageCacheKey(filterRequest.From, filterRequest.To);
        var rawRows = await GetRawUsageRowsAsync(
            cacheKey,
            filterRequest.From,
            filterRequest.To,
            cancellationToken);

        foreach (var row in rawRows)
        {
            Campaign campaign;
            if (_callFlowIdMapping.TryGetValue(row.CallFlowId, out campaign))
            {
                // Mapped campaign: include in results with full campaign/department/vendor data
                var vendor = _vendors.FirstOrDefault(v => v.Id == campaign.VendorId);
                var department = _departments.FirstOrDefault(d => d.Id == campaign.DepartmentId);

                result.Add(new UsageRecord
                {
                    Department = department?.Name ?? "Unknown",
                    Campaign = campaign.Name,
                    Vendor = vendor?.Name ?? "Unknown",
                    UniqueBase = row.UniqueBase,
                    TotalAttempts = row.TotalAttempts,
                    UniqueConnects = row.UniqueConnects,
                    TotalConnects = row.TotalConnects,
                    Mou = row.Mou,
                    Cost = null  // Will be computed by UsageService using vendor rate + GST
                });
            }
            else
            {
                // Unmapped Call Flow ID - track separately for explicit surfacing
                unmappedRows.Add((
                    row.CampaignName,
                    row.CallFlowId,
                    row.TotalAttempts,
                    row.UniqueBase,
                    row.TotalConnects,
                    row.UniqueConnects,
                    row.Mou));
            }
        }

        // Append unmapped Call Flow IDs as synthetic UsageRecord entries so they remain visible in results
        // (never silently dropped) to ensure unmapped billable usage doesn't disappear from cost totals
        foreach (var row in unmappedRows.OrderBy(x => x.CallFlowId))
        {
            result.Add(new UsageRecord
            {
                Department = "UNMAPPED",
                Campaign = string.IsNullOrWhiteSpace(row.CampaignName)
                    ? $"Unmapped Call Flow ID: {row.CallFlowId}"
                    : row.CampaignName,
                Vendor = "UNMAPPED",
                UniqueBase = row.UniqueBase,
                TotalAttempts = row.TotalAttempts,
                UniqueConnects = row.UniqueConnects,
                TotalConnects = row.TotalConnects,
                Mou = row.Mou,
                Cost = null
            });
        }

        return result;
    }

    private async Task<IReadOnlyList<RawUsageRow>> GetRawUsageRowsAsync(
        string cacheKey,
        DateTime? from,
        DateTime? to,
        CancellationToken cancellationToken)
    {
        if (RawUsageCache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.Rows;
            }

            RawUsageCache.TryRemove(cacheKey, out _);
        }

        var fetchLock = RawUsageFetchLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await fetchLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after waiting so concurrent requests share the first completed result.
            if (RawUsageCache.TryGetValue(cacheKey, out cached))
            {
                if (cached.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    return cached.Rows;
                }

                RawUsageCache.TryRemove(cacheKey, out _);
            }

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken);

                using (var command = new SqlCommand("dbo.sp_vb_cost_analysis_dg", connection))
                {
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandTimeout = 120;

                    // The procedure signature is: @FromDate DATETIME, @ToDate DATETIME.
                    command.Parameters.Add("@FromDate", SqlDbType.DateTime).Value =
                        (object?)from ?? DBNull.Value;
                    command.Parameters.Add("@ToDate", SqlDbType.DateTime).Value =
                        (object?)to ?? DBNull.Value;

                    var rows = new List<RawUsageRow>();
                    using (var reader = await command.ExecuteReaderAsync(
                        CommandBehavior.SequentialAccess,
                        cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            rows.Add(new RawUsageRow
                            {
                                CampaignName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                                CallFlowId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                TotalAttempts = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2)),
                                UniqueBase = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3)),
                                TotalConnects = reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)),
                                UniqueConnects = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                                Mou = reader.IsDBNull(6) ? null : Convert.ToInt64(reader.GetValue(6))
                            });
                        }
                    }

                    var cachedRows = (IReadOnlyList<RawUsageRow>)rows;
                    RawUsageCache[cacheKey] = new RawUsageCacheEntry(
                        cachedRows,
                        DateTimeOffset.UtcNow.Add(RawUsageCacheLifetime));
                    return cachedRows;
                }
            }
        }
        finally
        {
            fetchLock.Release();
        }
    }

    private static string CreateRawUsageCacheKey(DateTime? from, DateTime? to)
    {
        return $"usage:{from?.ToString("O") ?? "null"}:{to?.ToString("O") ?? "null"}";
    }

    private sealed class RawUsageCacheEntry
    {
        public RawUsageCacheEntry(IReadOnlyList<RawUsageRow> rows, DateTimeOffset expiresAt)
        {
            Rows = rows;
            ExpiresAt = expiresAt;
        }

        public IReadOnlyList<RawUsageRow> Rows { get; }
        public DateTimeOffset ExpiresAt { get; }
    }

    private sealed class RawUsageRow
    {
        public string CampaignName { get; set; } = string.Empty;
        public string CallFlowId { get; set; } = string.Empty;
        public int? TotalAttempts { get; set; }
        public int? UniqueBase { get; set; }
        public int? TotalConnects { get; set; }
        public int? UniqueConnects { get; set; }
        public long? Mou { get; set; }
    }
}
