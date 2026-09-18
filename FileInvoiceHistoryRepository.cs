using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VoicebotBillingMIS.Data.Compatibility;
using VoicebotBillingMIS.Data.Models;

namespace VoicebotBillingMIS.Infrastructure;

public sealed class FileInvoiceHistoryRepository : IInvoiceHistoryRepository
{
    private readonly string _directoryPath;

    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        Converters = { new DateOnlyJsonConverter() }
    };

    public FileInvoiceHistoryRepository(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("History directory path is required.", nameof(directoryPath));
        }

        _directoryPath = directoryPath;
    }

    public Task SaveAsync(
        InvoiceHistoryRecord history,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory();

        var path = GetPath(history.Id);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonConvert.SerializeObject(history, Formatting.Indented, JsonSettings));
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InvoiceHistoryRecord>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory();

        var records = new List<InvoiceHistoryRecord>();
        foreach (var path in Directory.EnumerateFiles(_directoryPath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = JsonConvert.DeserializeObject<InvoiceHistoryRecord>(
                File.ReadAllText(path),
                JsonSettings);
            if (history is not null)
            {
                records.Add(history);
            }
        }

        return Task.FromResult<IReadOnlyList<InvoiceHistoryRecord>>(
            records.OrderByDescending(r => r.ComparedUtc).ToList());
    }

    public Task<InvoiceHistoryRecord?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(id);
        if (!File.Exists(path))
        {
            return Task.FromResult<InvoiceHistoryRecord?>(null);
        }

        var history = JsonConvert.DeserializeObject<InvoiceHistoryRecord>(
            File.ReadAllText(path),
            JsonSettings);
        return Task.FromResult(history);
    }

    public Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(id);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public async Task<bool> IsDuplicateAsync(
        InvoiceHistoryRecord history,
        CancellationToken cancellationToken = default)
    {
        var records = await GetAllAsync(cancellationToken);
        return records.Any(existing =>
            string.Equals(existing.VendorName, history.VendorName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Comparison?.Record?.OriginalFileName,
                history.Comparison?.Record?.OriginalFileName, StringComparison.OrdinalIgnoreCase) &&
            SameRows(existing.Comparison?.ReconciliationRows, history.Comparison?.ReconciliationRows));
    }

    public async Task<int> DeleteRowsForMonthAsync(
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        var deletedRows = 0;
        foreach (var history in await GetAllAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = history.Comparison?.ReconciliationRows;
            if (rows == null || rows.Count == 0)
            {
                continue;
            }

            var remaining = rows.Where(row =>
                !row.FromDate.HasValue ||
                row.FromDate.Value.Year != year ||
                row.FromDate.Value.Month != month).ToList();
            if (remaining.Count == rows.Count)
            {
                continue;
            }

            deletedRows += rows.Count - remaining.Count;
            history.Comparison.ReconciliationRows = remaining;
            history.RowCount = remaining.Count;
            if (remaining.Count == 0)
            {
                await DeleteAsync(history.Id, cancellationToken);
            }
            else
            {
                await SaveAsync(history, cancellationToken);
            }
        }

        return deletedRows;
    }

    private static bool SameRows(
        IReadOnlyList<InvoiceReconciliationRow>? left,
        IReadOnlyList<InvoiceReconciliationRow>? right)
    {
        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        var leftKeys = left.Select(RowKey).OrderBy(key => key, StringComparer.Ordinal).ToList();
        var rightKeys = right.Select(RowKey).OrderBy(key => key, StringComparer.Ordinal).ToList();
        return leftKeys.SequenceEqual(rightKeys, StringComparer.Ordinal);
    }

    private static string RowKey(InvoiceReconciliationRow row)
        => string.Join("|",
            row.BillCampaignName ?? string.Empty,
            row.BillMou?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            row.InternalCampaignId?.ToString() ?? string.Empty,
            row.FromDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            row.ToDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            row.TrackedMou?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

    private string GetPath(Guid id)
        => Path.Combine(_directoryPath, id.ToString("N") + ".json");

    private void EnsureDirectory()
        => Directory.CreateDirectory(_directoryPath);
}
