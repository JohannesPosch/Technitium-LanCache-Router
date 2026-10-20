using System.Text.Json;

namespace LanCacheRouter.DomainList;

/// <summary>
/// Persists the last good <see cref="RawDomainList"/> in the app's folder so DNS keeps working after a restart
/// while GitHub is unreachable (ADR 0002). Each cluster node has its own file.
/// </summary>
public sealed class DomainSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly string _path;
    private readonly Action<string> _log;

    /// <summary>
    /// Creates a store.
    /// </summary>
    /// <param name="path">Full path of the snapshot file; its folder is created on first save.</param>
    /// <param name="log">Receives a message if a snapshot cannot be read or written.</param>
    public DomainSnapshotStore(string path, Action<string> log)
    {
        _path = path;
        _log = SafeLog.Wrap(log);
    }

    /// <summary>
    /// Writes the list atomically (temp file, then move), so a crash never leaves a half-written snapshot.
    /// Failures are logged, not thrown: not being able to persist must not affect DNS.
    /// </summary>
    /// <param name="list">The list to save.</param>
    public void Save(RawDomainList list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"Domain list: could not save snapshot to {_path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the saved list. Patterns are validated again, because the file could have been edited or damaged.
    /// </summary>
    /// <returns>The list, or <c>null</c> if there is no usable snapshot.</returns>
    public RawDomainList? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            RawDomainList? list = JsonSerializer.Deserialize<RawDomainList>(File.ReadAllText(_path), Options);
            if (list is null || list.Services is null || string.IsNullOrWhiteSpace(list.BaseUrl))
                return null;

            return list with
            {
                Services =
                [
                    .. list.Services.Where(s => !string.IsNullOrWhiteSpace(s.Name) && s.Patterns is not null).Select(s => new ServiceDomains(
                        s.Name,
                        [.. s.Patterns.Select(p => DomainName.TryNormalizePattern(p, out string normalized, out _) ? normalized : null).OfType<string>()]))
                ]
            };
        }
        catch (Exception ex)
        {
            // Whatever is wrong with the file (unreadable, damaged, edited by hand), it must not stop the app from starting.
            _log($"Domain list: ignoring unreadable snapshot {_path}: {SafeLog.Escape(ex.Message)}");
            return null;
        }
    }
}
