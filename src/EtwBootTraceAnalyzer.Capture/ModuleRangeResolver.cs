namespace EtwBootTraceAnalyzer.Capture;

/// <summary>
/// Resolves a code address to the module (driver/DLL) that owns it, from ImageLoad ranges seen
/// during the same session. This is module-level attribution only ("which driver"), the same
/// granularity WPA gives you without a symbol server - getting to a specific routine name needs
/// PDBs, which is out of scope here.
/// </summary>
internal sealed class ModuleRangeResolver
{
    private readonly List<(ulong Base, ulong End, string Name)> _ranges = [];
    private bool _sorted;

    public void AddModule(ulong imageBase, long imageSize, string fileName)
    {
        if (imageSize <= 0)
        {
            return;
        }
        _ranges.Add((imageBase, imageBase + (ulong)imageSize, ShortName(fileName)));
        _sorted = false;
    }

    public string Resolve(ulong address)
    {
        if (!_sorted)
        {
            _ranges.Sort((a, b) => a.Base.CompareTo(b.Base));
            _sorted = true;
        }

        var lo = 0;
        var hi = _ranges.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var (@base, end, name) = _ranges[mid];
            if (address < @base)
            {
                hi = mid - 1;
            }
            else if (address >= end)
            {
                lo = mid + 1;
            }
            else
            {
                return name;
            }
        }
        return $"0x{address:x}";
    }

    private static string ShortName(string fileName) =>
        string.IsNullOrEmpty(fileName) ? "unknown" : Path.GetFileName(fileName);
}
