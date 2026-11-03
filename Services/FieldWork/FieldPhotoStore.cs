using System.Text.RegularExpressions;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work meter photos on disk (as MaintenancePhotoStore): outside wwwroot, under a new GUID name, type decided from
    // the file's own first bytes. Only JPEG is accepted -- the browser page always produces a compressed JPEG from the live
    // camera (wwwroot/js/fieldwork-capture.js). Photos are served only through an authorized page handler
    // (FieldWork/DayDetails?handler=Photo), never as static files.
    public class FieldPhotoStore
    {
        public const string JpegContentType = "image/jpeg";

        private static readonly Regex StoredNamePattern = new("^[0-9a-f]{32}\\.jpg$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public string Root { get; }
        public long MaxBytes { get; }

        public FieldPhotoStore(FieldWorkOptions options)
        {
            Root = options.ResolvePhotoRoot();
            MaxBytes = options.MaxPhotoBytes > 0 ? options.MaxPhotoBytes : 2L * 1024 * 1024;
        }

        // null = acceptable, otherwise the message for the user.
        public string? Validate(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "The photo is empty. Take the photo again.";
            if (bytes.Length > MaxBytes)
                return $"The photo is too large (at most {MaxBytes / 1024 / 1024.0:0.#} MB).";
            var type = MaintenancePhotoStore.Detect(bytes.AsSpan(0, Math.Min(12, bytes.Length)));
            if (type?.ContentType != JpegContentType)
                return "Only a JPEG photo from the camera is accepted.";
            // a JPEG ends with the EOI marker FF D9 -- a truncated upload is refused
            if (bytes.Length < 4 || bytes[^2] != 0xFF || bytes[^1] != 0xD9)
                return "The photo upload was incomplete. Take the photo again.";
            return null;
        }

        public async Task<string> SaveAsync(byte[] bytes)
        {
            Directory.CreateDirectory(Root);
            var stored = $"{Guid.NewGuid():N}.jpg";
            await using var target = new FileStream(PathFor(stored)!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await target.WriteAsync(bytes);
            return stored;
        }

        public string? PathFor(string? storedFileName)
        {
            if (storedFileName == null || !StoredNamePattern.IsMatch(storedFileName))
                return null;
            var full = Path.GetFullPath(Path.Combine(Root, storedFileName));
            var root = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        public void DeleteQuietly(string? storedFileName)
        {
            try
            {
                var path = PathFor(storedFileName);
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
