using BitBroom.Rescue.Core.Carving;
using BitBroom.Rescue.Core.Health;
using BitBroom.Rescue.Core.Imaging;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Recovery;
using BitBroom.Rescue.Core.RecycleBin;
using BitBroom.Rescue.Core.Vss;

// ============================================================================
// BitBroom Rescue CLI — safety-first, READ-ONLY data recovery.
//   list                                  list volumes and physical disks
//   scan   <C|\path\to.img> [--min-size N] [--json] [--top N]
//   carve  <C|\path\to.img> [--json] [--top N]
//   recover <C|\path\to.img> --out <dir> [--deleted] [--carve] [--min-size N]
// A scan/carve never writes to the source. 'recover' writes ONLY to --out, which
// must be a different drive than the source.
// ============================================================================

try
{
    return Run(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static int Run(string[] args)
{
    if (args.Length == 0)
    {
        PrintHelp();
        return 0;
    }

    string command = args[0].ToLowerInvariant();
    return command switch
    {
        "list" => CommandList(),
        "scan" => CommandScan(args),
        "carve" => CommandCarve(args),
        "recover" => CommandRecover(args),
        "image" => CommandImage(args),
        "health" => CommandHealth(args),
        "bin" => CommandBin(args),
        "previous" => CommandPrevious(args),
        "version" => CommandVersion(),
        "help" or "--help" or "-h" => Help(),
        _ => Unknown(command),
    };
}

static int CommandList()
{
    Console.WriteLine("Volumes:");
    foreach (StorageDevice d in DeviceEnumerator.ListVolumes())
    {
        Console.WriteLine($"  {d.DriveLetter}:  {Fmt(d.SizeBytes),10}  {d.FileSystem,-6}  {d.Label}");
    }

    Console.WriteLine("\nPhysical disks (need admin to read):");
    try
    {
        foreach (StorageDevice d in DeviceEnumerator.ListPhysicalDisks())
        {
            Console.WriteLine($"  \\\\.\\PhysicalDrive{d.DiskNumber}  {Fmt(d.SizeBytes),10}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("  (could not enumerate: " + ex.Message + ")");
    }

    return 0;
}

static int CommandScan(string[] args)
{
    string? target = Positional(args);
    if (target is null)
    {
        Console.Error.WriteLine("Usage: bitbroom-rescue scan <C|\\path\\to.img> [--min-size N] [--top N] [--json]");
        return 3;
    }

    long minSize = LongOpt(args, "--min-size", 1);
    int top = (int)LongOpt(args, "--top", 40);

    using var session = new RecoverySession(OpenTarget(target));
    Console.WriteLine($"Detected file system: {session.FileSystem}");
    if (session.FileSystem == DetectedFileSystem.Unknown)
    {
        Console.Error.WriteLine("Unknown/unsupported file system for metadata scan — try 'carve' instead.");
        return 2;
    }

    Console.WriteLine($"Scanning {session.SourceDescription} for deleted files (read-only)…");
    var progress = new Progress<ScanProgress>(p =>
        Console.Write($"\r  {p.RecordsSeen:N0} records, {p.Found:N0} deleted found…   "));
    List<RecoverableItem> items = session.ScanDeletedFiles(minSize, progress);
    Console.WriteLine();

    items.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
    Console.WriteLine($"\n{items.Count:N0} deleted files recoverable.\n");
    foreach (RecoverableItem it in items.Take(top))
    {
        Console.WriteLine($"  {Fmt(it.SizeBytes),10}  [{it.Confidence,-4}]  {it.ModifiedUtc:yyyy-MM-dd}  {it.OriginalPath ?? it.Name}");
    }

    if (items.Count > top)
    {
        Console.WriteLine($"  …and {items.Count - top:N0} more (raise --top or use recover).");
    }

    return 0;
}

static int CommandCarve(string[] args)
{
    string? target = Positional(args);
    if (target is null)
    {
        Console.Error.WriteLine("Usage: bitbroom-rescue carve <C|\\path\\to.img> [--top N]");
        return 3;
    }

    int top = (int)LongOpt(args, "--top", 40);
    using ISectorSource source = OpenTarget(target);
    Console.WriteLine($"Carving {source.Description} by file signature (read-only)…");
    var carver = new FileCarver(source);
    var progress = new Progress<CarveProgress>(p =>
        Console.Write($"\r  {Fmt(p.BytesScanned)}/{Fmt(p.BytesTotal)} scanned, {p.Found} found…   "));
    List<RecoverableItem> items = carver.Carve(progress: progress);
    Console.WriteLine();

    var byType = items.GroupBy(i => i.Extension).OrderByDescending(g => g.Count());
    Console.WriteLine($"\n{items.Count:N0} files carved.\n");
    foreach (var g in byType)
    {
        Console.WriteLine($"  {g.Count(),6}  .{g.Key}");
    }

    return 0;
}

static int CommandRecover(string[] args)
{
    string? target = Positional(args);
    string? outDir = StrOpt(args, "--out");
    if (target is null || outDir is null)
    {
        Console.Error.WriteLine("Usage: bitbroom-rescue recover <C|\\path\\to.img> --out <dir> [--deleted] [--carve] [--min-size N]");
        return 3;
    }

    bool doDeleted = HasFlag(args, "--deleted") || !HasFlag(args, "--carve");
    bool doCarve = HasFlag(args, "--carve");
    long minSize = LongOpt(args, "--min-size", 1);

    string? sourceRoot = target.Length == 1 && char.IsLetter(target[0]) ? $"{char.ToUpperInvariant(target[0])}:\\" : null;
    string? refusal = RecoveryDestinationGuard.Validate(sourceRoot, outDir);
    if (refusal is not null)
    {
        Console.Error.WriteLine("Refusing to recover: " + refusal);
        return 5;
    }

    using ISectorSource source = OpenTarget(target);
    var all = new List<RecoverableItem>();

    using (var session = new RecoverySession(source, ownsSource: false))
    {
        if (doDeleted)
        {
            Console.WriteLine($"Scanning deleted files ({session.FileSystem})…");
            all.AddRange(session.ScanDeletedFiles(minSize));
        }

        if (doCarve)
        {
            Console.WriteLine("Carving by signature…");
            all.AddRange(session.Carve());
        }
    }

    Console.WriteLine($"Writing {all.Count:N0} item(s) to {outDir}…");
    var writer = new RecoveryWriter(outDir, sourceRoot);
    RecoveryWriteResult result = writer.WriteAll(all,
        new Progress<(int Done, string Name)>(p => Console.Write($"\r  {p.Done:N0} written…   ")));
    Console.WriteLine();
    Console.WriteLine($"\nRecovered {result.Written:N0} files ({Fmt(result.BytesWritten)}), {result.Failed} failed.");
    Console.WriteLine($"Log: {result.LogPath}");
    return result.Failed == 0 ? 0 : 2;
}

static int CommandImage(string[] args)
{
    string? target = Positional(args);
    string? outPath = StrOpt(args, "--out");
    if (target is null || outPath is null)
    {
        Console.Error.WriteLine("Usage: bitbroom-rescue image <C|\\\\.\\PhysicalDriveN> --out <image.img> [--retries N]");
        return 3;
    }

    int retries = (int)LongOpt(args, "--retries", 2);
    using ISectorSource source = OpenTarget(target);
    Console.WriteLine($"Imaging {source.Description} → {outPath} (read-only source, clone-first)…");
    var imager = new DiskImager(source);
    var progress = new Progress<ImagingProgress>(p =>
        Console.Write($"\r  {p.Phase}: {Fmt(p.BytesCopied)}/{Fmt(p.BytesTotal)}  bad={Fmt(p.BadBytes)}  {p.RateMBps:0.0} MB/s   "));
    ImagingResult result = imager.CreateImage(outPath, retryPasses: retries, progress: progress);
    Console.WriteLine();
    Console.WriteLine($"\nImaged {Fmt(result.BytesCopied)}; {Fmt(result.BadBytes)} unreadable in {result.BadRegions} region(s).");
    Console.WriteLine($"Map: {result.MapPath}");
    Console.WriteLine("Now scan/carve the IMAGE, never the failing drive again.");
    return 0;
}

static int CommandHealth(string[] args)
{
    string? target = Positional(args);
    if (target is null || !(target.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase) || int.TryParse(target, out _)))
    {
        Console.Error.WriteLine("Usage: bitbroom-rescue health <diskNumber>   (e.g. 'health 0')");
        return 3;
    }

    int disk = int.TryParse(target, out int d) ? d : int.Parse(target.AsSpan(@"\\.\PhysicalDrive".Length));
    DriveHealthInfo info = DiskHealthProbe.Probe(disk);
    Console.WriteLine($"PhysicalDrive{disk}: media={info.Media}, SMART-predicts-failure={Show(info.SmartPredictsFailure)}, TRIM={Show(info.TrimEnabled)}\n");
    foreach (HealthWarning w in HealthAdvisor.Evaluate(info, RecoveryScenario.DeletedFiles))
    {
        Console.WriteLine($"[{w.Severity}] {w.Title}\n    {w.Detail}\n");
    }

    return 0;

    static string Show(bool? b) => b is null ? "unknown" : b.Value ? "yes" : "no";
}

static int CommandBin(string[] args)
{
    string? target = Positional(args);
    if (target is null || target.Length != 1 || !char.IsLetter(target[0]))
    {
        Console.Error.WriteLine("Usage: bitbroom-rescue bin <driveLetter>   (e.g. 'bin C')");
        return 3;
    }

    List<RecoverableItem> items = RecycleBinScanner.ScanVolume(target[0]);
    Console.WriteLine($"{items.Count:N0} intact file(s) in the Recycle Bin of {char.ToUpperInvariant(target[0])}:\n");
    foreach (RecoverableItem it in items.OrderByDescending(i => i.SizeBytes).Take((int)LongOpt(args, "--top", 40)))
    {
        Console.WriteLine($"  {Fmt(it.SizeBytes),10}  {it.ModifiedUtc:yyyy-MM-dd}  {it.OriginalPath}");
    }

    return 0;
}

static int CommandPrevious(string[] args)
{
    string? target = Positional(args);
    string? rel = args.Length >= 3 && !args[2].StartsWith('-') ? args[2] : null;
    if (target is null || target.Length != 1 || rel is null)
    {
        Console.Error.WriteLine("Usage: bitbroom-rescue previous <driveLetter> <path\\relative\\to\\volume>");
        Console.Error.WriteLine("  e.g. bitbroom-rescue previous C Users\\me\\Documents\\report.docx");
        return 3;
    }

    List<RecoverableItem> versions = ShadowCopyService.FindPreviousVersions(target[0], rel);
    if (versions.Count == 0)
    {
        Console.WriteLine("No shadow-copy previous versions found (VSS may be off, or none contain this file). Needs admin.");
        return 0;
    }

    Console.WriteLine($"{versions.Count} previous version(s) of {rel}:\n");
    foreach (RecoverableItem v in versions.OrderByDescending(x => x.ModifiedUtc))
    {
        Console.WriteLine($"  {Fmt(v.SizeBytes),10}  {v.OriginalPath}");
    }

    return 0;
}

static int CommandVersion()
{
    Console.WriteLine("BitBroom Rescue CLI 1.0.0");
    return 0;
}

static ISectorSource OpenTarget(string target)
{
    if (target.Length == 1 && char.IsLetter(target[0]))
    {
        return RawDeviceSource.OpenVolume(target[0]);
    }

    if (target.StartsWith(@"\\.\PhysicalDrive", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(target.AsSpan(@"\\.\PhysicalDrive".Length), out int disk))
    {
        return RawDeviceSource.OpenPhysicalDisk(disk);
    }

    return new ImageFileSource(target);
}

static void PrintHelp() => Help();

static int Help()
{
    Console.WriteLine("""
        BitBroom Rescue — safety-first, read-only data recovery for Windows.

        USAGE
          bitbroom-rescue list
          bitbroom-rescue scan    <C | \path\to.img>  [--min-size N] [--top N]
          bitbroom-rescue carve   <C | \path\to.img>  [--top N]
          bitbroom-rescue recover <C | \path\to.img>  --out <dir> [--deleted] [--carve] [--min-size N]
          bitbroom-rescue image   <C | \\.\PhysicalDriveN>  --out <image.img> [--retries N]
          bitbroom-rescue health  <diskNumber>
          bitbroom-rescue bin      <driveLetter>
          bitbroom-rescue previous <driveLetter> <path\relative\to\volume>
          bitbroom-rescue version

        Scanning is always read-only. 'recover' writes ONLY to --out, which must be a
        DIFFERENT drive than the source (enforced). Volume/disk targets need Administrator.
        """);
    return 0;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"Unknown command '{cmd}'. Run 'bitbroom-rescue help'.");
    return 3;
}

// ---- tiny arg helpers ----
static string? Positional(string[] args) => args.Length >= 2 && !args[1].StartsWith('-') ? args[1] : null;
static bool HasFlag(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
static string? StrOpt(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static long LongOpt(string[] args, string name, long fallback)
    => StrOpt(args, name) is { } s && long.TryParse(s, out long v) ? v : fallback;

static string Fmt(long b)
{
    string[] u = ["B", "KB", "MB", "GB", "TB"];
    double s = b;
    int i = 0;
    while (s >= 1024 && i < u.Length - 1)
    {
        s /= 1024;
        i++;
    }

    return $"{s:0.#} {u[i]}";
}
