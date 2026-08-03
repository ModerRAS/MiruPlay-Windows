# Windows Recognition And Metadata Core

The Windows core now exposes three reusable boundaries:

- `IAnimeVideoClassifier` and `AnimeVideoClassifier` combine the existing filename rules with the canonical AniFileBERT ONNX parser. `CloudDriveLibraryOrganizer` accepts this boundary through its optional constructor parameter and defaults to the shared lazy instance.
- `NfoDocumentService` reads and writes episode and `tvshow.nfo` documents under a validated local source root. It rejects traversal and existing reparse points, uses bounded secure XML parsing, and writes through a same-directory temporary file.
- `BangumiArchiveStore` owns the archive lifecycle: latest manifest lookup, bounded download/import, SHA-256 verification, staged `subject.jsonlines` replacement, bounded validation, and season-aware subject search.

`DirectoryLibraryIndex` should accept the same `IAnimeVideoClassifier` in the next integration wave and pass the normalized file path, file name, and parent context to `Classify`. The current directory-index slice remains unchanged in this wave. `MediaSourceRegistry` should remain the owner of source-level dependency construction and scan scheduling.

The model assets are copied from the Android canonical artifact into `src/MiruPlay.Windows/Assets/anime_parser/` and declared as output/publish content. The ONNX runtime is intentionally isolated to `OnnxAnimeFilenameParser`; callers can inject a test parser or classifier without loading the model.
