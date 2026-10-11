namespace PlantStockManager.Services
{
    // Configuration: "SuggestionStorage": { "PhotoRoot": "<folder>" }.
    // Empty = <LocalApplicationData>/PlantStockManager/SuggestionPhotos -- outside wwwroot and
    // outside the publish folder (like Maintenance photos), so a redeploy never removes photos.
    public class SuggestionStorageOptions
    {
        public string? PhotoRoot { get; set; }

        public string ResolvePhotoRoot()
            => Path.GetFullPath(!string.IsNullOrWhiteSpace(PhotoRoot)
                ? PhotoRoot
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantStockManager", "SuggestionPhotos"));
    }

    // Suggestions & Problems photos: EXACTLY the Maintenance photo rules (MaintenancePhotoStore) --
    // type from the file's own bytes (JPEG / PNG / WebP), 5 MB, a new GUID file name, reads only
    // of names this store created and only inside its root -- in a folder of its own.
    public class SuggestionPhotoStore : MaintenancePhotoStore
    {
        public SuggestionPhotoStore(SuggestionStorageOptions options)
            : base(new MaintenanceStorageOptions { PhotoRoot = options.ResolvePhotoRoot() })
        {
        }

        // Saves the photo (if any), then runs the database write. If the write fails the saved
        // file is removed again, so a failed post never leaves an orphaned file behind.
        public async Task<T> SaveThenAsync<T>(IFormFile? file, DetectedType? type, Func<SavedPhoto?, Task<T>> write)
        {
            SavedPhoto? saved = null;
            try
            {
                if (file != null && type != null)
                    saved = await SaveAsync(file, type);
                return await write(saved);
            }
            catch
            {
                if (saved != null)
                    DeleteQuietly(new[] { saved.StoredFileName });
                throw;
            }
        }
    }
}
