using System.Text.RegularExpressions;

namespace PlantStockManager.Services
{
    // Configuration: "MaintenanceStorage": { "PhotoRoot": "<folder>" }.
    // Empty = <LocalApplicationData>/PlantStockManager/MaintenancePhotos -- outside wwwroot and
    // outside the publish folder, so a redeploy never removes photos. Production should set an
    // explicit persistent folder (and back it up with the database).
    public class MaintenanceStorageOptions
    {
        public string? PhotoRoot { get; set; }

        public string ResolvePhotoRoot()
            => Path.GetFullPath(!string.IsNullOrWhiteSpace(PhotoRoot)
                ? PhotoRoot
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantStockManager", "MaintenancePhotos"));
    }

    // Maintenance ticket photos on disk. The file TYPE is decided from the file's own first
    // bytes (JPEG / PNG / WebP signatures) -- never from the uploaded name or the browser's
    // Content-Type. Files are stored under a new GUID name (32 hex + extension) in one flat
    // folder; the original name is kept only as display text in the database. Reading back
    // accepts only that GUID pattern and re-checks that the full path stays inside the root,
    // so no stored or requested name can reach any other file (path traversal).
    public class MaintenancePhotoStore
    {
        public const long MaxBytes = 5L * 1024 * 1024;
        public const int MaxOriginalNameLength = 255;
        public static readonly IReadOnlyList<string> AllowedContentTypes = new[] { "image/jpeg", "image/png", "image/webp" };

        private static readonly Regex StoredNamePattern = new("^[0-9a-f]{32}\\.(jpg|png|webp)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public string Root { get; }

        public MaintenancePhotoStore(MaintenanceStorageOptions options)
        {
            Root = options.ResolvePhotoRoot();
        }

        public sealed record DetectedType(string ContentType, string Extension);

        public sealed record SavedPhoto(string StoredFileName, string OriginalFileName, string ContentType, int SizeBytes);

        // JPEG FF D8 FF; PNG 89 50 4E 47 0D 0A 1A 0A; WebP "RIFF" ???? "WEBP". Anything else
        // (renamed .exe, SVG, HTML, GIF, PDF ...) is rejected.
        public static DetectedType? Detect(ReadOnlySpan<byte> header)
        {
            if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return new DetectedType("image/jpeg", "jpg");
            if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
                return new DetectedType("image/png", "png");
            if (header.Length >= 12 && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
                return new DetectedType("image/webp", "webp");
            return null;
        }

        // Display name only: no folders, no control characters, at most 255 characters.
        public static string SafeOriginalName(string? fileName)
        {
            var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Split('/').Last());
            name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length == 0) name = "photo";
            return name.Length > MaxOriginalNameLength ? name[..MaxOriginalNameLength] : name;
        }

        // Validates one upload: size and real content. Returns an error message or the detected type.
        public static async Task<(DetectedType? Type, string? Error)> ValidateAsync(IFormFile file)
        {
            var label = SafeOriginalName(file.FileName);
            if (file.Length <= 0)
                return (null, $"{label}: the file is empty.");
            if (file.Length > MaxBytes)
                return (null, $"{label}: a photo can be at most 5 MB.");
            var header = new byte[12];
            await using var stream = file.OpenReadStream();
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
            var type = Detect(header.AsSpan(0, read));
            return type == null ? (null, $"{label}: only JPEG, PNG or WebP photos are allowed.") : (type, null);
        }

        // Writes a validated upload under a new GUID name (never overwrites).
        public async Task<SavedPhoto> SaveAsync(IFormFile file, DetectedType type)
        {
            Directory.CreateDirectory(Root);
            var stored = $"{Guid.NewGuid():N}.{type.Extension}";
            var path = PathFor(stored)!;
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var source = file.OpenReadStream())
            {
                await source.CopyToAsync(target);
            }
            return new SavedPhoto(stored, SafeOriginalName(file.FileName), type.ContentType, (int)file.Length);
        }

        // Full path of a stored name, or null when the name is not one this store created.
        public string? PathFor(string? storedFileName)
        {
            if (storedFileName == null || !StoredNamePattern.IsMatch(storedFileName))
                return null;
            var full = Path.GetFullPath(Path.Combine(Root, storedFileName));
            var root = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        // Removes files whose database rows were never written (the save transaction failed).
        public void DeleteQuietly(IEnumerable<string> storedFileNames)
        {
            foreach (var name in storedFileNames)
            {
                try
                {
                    var path = PathFor(name);
                    if (path != null && File.Exists(path))
                        File.Delete(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
