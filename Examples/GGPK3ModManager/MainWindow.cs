using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using LibBundledGGPK3;
using LibBundle3;
using LibBundle3.Nodes;
using FileRecord = LibBundle3.Records.FileRecord;

namespace GGPK3ModManager
{
    public sealed class MainWindow : Form
    {
        private Dictionary<string, string[]> presetMods;
        private StackLayout modsCheckBoxLayout;
        private TextArea logArea;
        private string logFilePath;
        private BundledGGPK? Ggpk;
        internal LibBundle3.Index? Index;
        private ProgressBar progressBar;
        private Label statusLabel;

        public MainWindow(string? path = null)
        {
            Title = "GGPK3 Mod Manager";
            MinimumSize = new Size(450, 400);
            Padding = new Padding(5);

            InitializeLogger();
            InitializeUI();
            LoadModsFromJson();
            InitializeCheckBoxes();

            LoadComplete += async (sender, e) => await InitializeGGPK(path);
        }

        private void InitializeUI()
        {
            // Controls
            modsCheckBoxLayout = new StackLayout
            {
                Spacing = 2,
                Padding = new Padding(3)
            };

            var removeButton = new Button { Text = "Apply Selected", Width = 120 };
            var restoreSelectedButton = new Button { Text = "Restore Selected", Width = 120 };
            var resetButton = new Button { Text = "Restore All", Width = 120 };

            logArea = new TextArea
            {
                ReadOnly = true,
                Font = Fonts.Monospace(9),
                Height = 150
            };

            progressBar = new ProgressBar
            {
                Visible = false,
                Height = 20
            };

            statusLabel = new Label
            {
                Text = "Ready",
                VerticalAlignment = VerticalAlignment.Center
            };

            // Layout
            var mainLayout = new DynamicLayout
            {
                Spacing = new Size(5, 5),
                Padding = new Padding(5)
            };

            mainLayout.BeginVertical();

            // Mods Section
            mainLayout.AddRow(new GroupBox
            {
                Text = "Available Mods",
                Content = new Scrollable
                {
                    Content = modsCheckBoxLayout,
                    Height = 150
                }
            });

            // Buttons
            var buttonRow = new TableLayout
            {
                Spacing = new Size(10, 5),
                Padding = new Padding(5)
            };

            buttonRow.Rows.Add(new TableRow(
                removeButton,
                restoreSelectedButton,
                resetButton
            ));


            mainLayout.AddRow(buttonRow);

            // Progress
            mainLayout.AddRow(new GroupBox
            {
                Text = "Progress",
                Content = new TableLayout(
                    progressBar,
                    new TableRow(statusLabel)
                )
            });

            // Logs
            mainLayout.AddRow(new GroupBox
            {
                Text = "Operation Log",
                Content = logArea
            });

            mainLayout.EndVertical();

            // Events
            removeButton.Click += async (s, e) => await OnRemoveSelectedClicked();
            restoreSelectedButton.Click += async (s, e) => await OnRestoreSelectedClicked();
            resetButton.Click += async (s, e) => await OnResetToDefaultClicked();

            Content = mainLayout;
        }

        private void LoadModsFromJson()
        {
            try
            {
                var jsonFilePath = "content/presets.json";
                if (File.Exists(jsonFilePath))
                {
                    var jsonContent = File.ReadAllText(jsonFilePath);
                    presetMods = JsonSerializer.Deserialize<Dictionary<string, string[]>>(jsonContent);

                    if (presetMods != null)
                    {
                        InitializeCheckBoxes();
                    }
                    else
                    {
                        Log("Failed to load mods from JSON.");
                    }
                }
                else
                {
                    Log("presets.json not found.");
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading JSON: {ex.Message}");
            }
        }

        private void InitializeLogger()
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            logFilePath = Path.Combine("logs", $"log_{timestamp}.txt");
            Directory.CreateDirectory("logs");
        }

        private void InitializeCheckBoxes()
        {
            modsCheckBoxLayout.Items.Clear();
            foreach (var mod in presetMods.Keys)
            {
                var checkBox = new CheckBox { Text = mod };
                modsCheckBoxLayout.Items.Add(checkBox);
            }
        }

        private async Task InitializeGGPK(string? path)
        {
            try
            {
                UpdateStatus("Initializing...", 0);

                if (path == null || !File.Exists(path))
                {
                    using var ofd = new OpenFileDialog
                    {
                        FileName = "Content.ggpk",
                        Filters = { new FileFilter("GGPK/Index File", ".ggpk", ".bin") }
                    };
                    if (ofd.ShowDialog(this) != DialogResult.Ok) return;
                    path = ofd.FileName;
                }

                await Task.Run(async () =>
                {
                    if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    {
                        Index = new LibBundle3.Index(path, false);
                    }
                    else
                    {
                        Ggpk = new BundledGGPK(path, false);
                        Index = Ggpk.Index;
                    }

                    await Task.Run(() => Index.ParsePaths()).ConfigureAwait(false);

                }).ConfigureAwait(false);

                UpdateStatus("GGPK initialized", 100);
                Log("GGPK/Bundle initialized successfully");
            }
            catch (Exception ex)
            {
                Log($"Initialization error: {ex.Message}");
                UpdateStatus("Initialization failed", 0);
            }
        }

        private void UpdateStatus(string text, int progress)
        {
            Application.Instance.Invoke(() =>
            {
                statusLabel.Text = text;
                progressBar.Value = progress;
                progressBar.Visible = progress > 0;
            });
        }

        private async Task OnRemoveSelectedClicked()
        {
            var selectedMods = modsCheckBoxLayout.Children
                .OfType<CheckBox>()
                .Where(cb => cb.Checked ?? false)
                .Select(cb => cb.Text)
                .ToList();

            if (!selectedMods.Any())
            {
                Log("No mods selected!");
                return;
            }

            int totalFiles = selectedMods.Sum(mod => presetMods[mod].Length);
            if (totalFiles == 0)
            {
                Log("No files to process!");
                return;
            }

            UpdateStatus("Applying mods...", 0);
            progressBar.Visible = true;

            try
            {
                int processedFiles = 0;
                foreach (var modName in selectedMods)
                {
                    if (presetMods.TryGetValue(modName, out var paths))
                    {
                        foreach (var path in paths)
                        {
                            await ProcessModAsync(path, backup: true);
                            processedFiles++;
                            UpdateStatus($"Applying mods... ({processedFiles}/{totalFiles})",
                                (int)((processedFiles / (double)totalFiles * 100)));
                        }
                    }
                }
                Log("Selected mods applied successfully");
            }
            catch (Exception ex)
            {
                Log($"Error applying mods: {ex.Message}");
            }
            finally
            {
                UpdateStatus("Ready", 0);
            }
        }

        private async Task OnResetToDefaultClicked()
        {

            var result = MessageBox.Show(
               "Are you sure you want to restore all backups?",
               "Confirm Restore",
               MessageBoxButtons.YesNo,
               MessageBoxType.Question
           );

            if (result != DialogResult.Yes)
            {
                Log("Restore backups cancelled by user.");
                return;
            }

            try
            {
                var backupDir = Path.Combine("content", "backup");
                if (!Directory.Exists(backupDir))
                {
                    Log("No backups found!");
                    return;
                }

                UpdateStatus("Restoring backups...", 0);
                progressBar.Visible = true;

                await RestoreBackupsAsync(backupDir);

                UpdateStatus("Backups restored", 100);
                await Task.Delay(500);
                UpdateStatus("Ready", 0);

                Log("Original files restored");
            }
            catch (Exception ex)
            {
                Log($"Error restoring backups: {ex.Message}");
                UpdateStatus("Restoration failed", 0);
            }
        }

        private async Task RestoreFileAsync(string path)
        {
            try
            {
                path = path.Replace('\\', '/').Trim();
                var isGGPK = path.StartsWith("root/", StringComparison.OrdinalIgnoreCase);
                var cleanPath = path.Replace("root/", "").Replace("bundle/", "").TrimStart('/');

                Log($"Restoring: {path}");
                Log($"Clean path: {cleanPath}");
                Log($"Type: {(isGGPK ? "GGPK" : "Bundle")}");

                var backupPath = Path.Combine("content", "backup", isGGPK ? "ggpk" : "bundle", cleanPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(backupPath))
                {
                    Log($"Backup file not found: {backupPath}");
                    return;
                }

                var backupContent = await File.ReadAllBytesAsync(backupPath).ConfigureAwait(false);
                Log($"Backup file size: {backupContent.Length} bytes");
                Log($"Backup file hash: {ComputeSHA256(backupContent)}");

                if (isGGPK)
                {
                    if (Ggpk?.Root.TryFindNode(cleanPath, out var node) == true && node is IFileNode fileNode)
                    {
                        var fileRecord = fileNode.Record;
                        var oldHash = ComputeSHA256(fileRecord.Read().Span);

                        await Task.Run(() => fileRecord.Write(backupContent)).ConfigureAwait(false);
                        Log($"Restored GGPK file: {cleanPath}");

                        var newHash = ComputeSHA256(fileRecord.Read().Span);
                        //Log($"Restoration {(oldHash == newHash ? "FAILED" : "SUCCESSFUL")}");
                        Log($"Old hash: {oldHash}");
                        Log($"New hash: {newHash}");
                    }
                    else
                    {
                        Log($"GGPK file not found: {cleanPath}");
                    }
                }
                else
                {
                    if (Index == null)
                    {
                        Log("Bundle index not initialized");
                        return;
                    }

                    var normalizedPath = cleanPath.ToLowerInvariant();
                    if (Index.TryGetFile(normalizedPath, out var fileRecord))
                    {
                        var oldHash = ComputeSHA256(fileRecord.Read().Span);

                        await Task.Run(() =>
                        {
                            fileRecord.Write(backupContent);
                            Index.Save();
                        }).ConfigureAwait(false);

                        Log($"Restored Bundle file: {normalizedPath}");

                        var newHash = ComputeSHA256(fileRecord.Read().Span);
                        Log($"Restoration {(oldHash == newHash ? "FAILED" : "SUCCESSFUL")}");
                        Log($"Old hash: {oldHash}");
                        Log($"New hash: {newHash}");
                    }
                    else
                    {
                        Log($"Bundle file not found: {normalizedPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error restoring file: {ex.Message}");
            }
        }

        private async Task ProcessModAsync(string path, bool backup)
        {
            try
            {
                path = path.Replace('\\', '/').Trim();
                var originalPath = path;
                var isGGPK = path.StartsWith("root/", StringComparison.OrdinalIgnoreCase);
                var cleanPath = path.Replace("root/", "").Replace("bundle/", "").TrimStart('/');

                Log($"Processing: {originalPath}");
                Log($"Clean path: {cleanPath}");
                Log($"Type: {(isGGPK ? "GGPK" : "Bundle")}");

                var sourcePath = Path.Combine("content", "remove", path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(sourcePath))
                {
                    Log($"MOD FILE NOT FOUND: {sourcePath}");
                    return;
                }

                var newContent = File.ReadAllBytes(sourcePath);
                Log($"Mod file size: {newContent.Length} bytes");
                //Log($"Mod file hash: {ComputeSHA256(newContent)}");

                await Task.Run(async () =>
                {
                    if (isGGPK)
                    {
                        await ProcessGGPKFileAsync(cleanPath, sourcePath, backup, newContent);
                    }
                    else
                    {
                        await ProcessBundleFileAsync(cleanPath, sourcePath, backup, newContent);
                    }
                }).ConfigureAwait(false);

                Index?.Save();
                if (Ggpk != null)
                {
                    var stream = Ggpk.UnsafeGetStream();
                    stream.Flush();
                    Log("GGPK changes flushed");
                }

                Log("Changes committed successfully");
            }
            catch (Exception ex)
            {
                Log($"CRITICAL ERROR: {ex}");
            }
        }

        private async Task ProcessBundleFileAsync(string cleanPath, string sourcePath, bool backup, byte[] newContent)
        {
            if (Index == null)
            {
                Log("Bundle index not initialized");
                return;
            }

            var normalizedPath = cleanPath.Replace('\\', '/').ToLowerInvariant();
            if (!Index.TryGetFile(normalizedPath, out var fileRecord))
            {
                Log($"Bundle file not found: {normalizedPath}");
                Log($"Available files sample:");
                foreach (var f in Index.Files.Values
                    .Where(f => f.Path?.Contains(normalizedPath.Split('/')[0]) == true)
                    .Take(5))
                {
                    Log($"- {f.Path} ({f.Size} bytes)");
                }
                return;
            }

            Log($"Found in Bundle: {fileRecord.Path}");

            if (backup)
            {
                var backupPath = Path.Combine("content", "backup", "bundle", cleanPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(backupPath))
                {
                    await BackupFileAsync(fileRecord, backupPath);
                }
            }

            var oldHash = ComputeSHA256(fileRecord.Read().Span);
            fileRecord.Write(newContent);
            Index.Save();
            var newHash = ComputeSHA256(fileRecord.Read().Span);

            //Log($"Replacement {(oldHash == newHash ? "FAILED" : "SUCCESSFUL")}");
            Log($"Old hash: {oldHash}");
            Log($"New hash: {newHash}");
        }

        private async Task ProcessGGPKFileAsync(string cleanPath, string sourcePath, bool backup, byte[] newContent)
        {
            try
            {
                var searchPath = cleanPath.Replace('\\', '/').TrimStart('/');
                Log($"Searching in GGPK: {searchPath}");

                if (Ggpk?.Root.TryFindNode(searchPath, out var node) != true || node is not IFileNode fileNode)
                {
                    Log($"GGPK file not found: {searchPath}");
                    return;
                }

                var fileRecord = fileNode.Record;
                Log($"Found in GGPK: {fileRecord.Path}");

                if (backup)
                {
                    var backupPath = Path.Combine("content", "backup", "ggpk", searchPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(backupPath))
                    {
                        await BackupFileAsync(fileRecord, backupPath);
                    }
                }

                var oldContent = fileRecord.Read();
                Log($"Original file size: {oldContent.Length} bytes");
                Log($"Original file hash: {ComputeSHA256(oldContent.Span)}");

                fileRecord.Write(newContent);

                var newContentVerify = fileRecord.Read();
                Log($"New file size: {newContentVerify.Length} bytes");
                Log($"New file hash: {ComputeSHA256(newContentVerify.Span)}");

                var stream = Ggpk.UnsafeGetStream();
                stream.Flush();
                Log("GGPK changes flushed");

                Log($"Successfully replaced: {searchPath}");
            }
            catch (Exception ex)
            {
                Log($"ERROR PROCESSING GGPK FILE: {ex}");
            }
        }

        private async Task BackupFileAsync(FileRecord fileRecord, string backupPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                var content = fileRecord.Read();
                await File.WriteAllBytesAsync(backupPath, content.ToArray());
                Log($"Backup created: {backupPath}");
                Log($"Backup size: {content.Length} bytes");
                Log($"Backup hash: {ComputeSHA256(content.Span)}");
            }
            catch (Exception ex)
            {
                Log($"BACKUP FAILED: {ex.Message}");
            }
        }

        private async Task OnRestoreSelectedClicked()
        {
            var selectedMods = modsCheckBoxLayout.Children
                .OfType<CheckBox>()
                .Where(cb => cb.Checked ?? false)
                .Select(cb => cb.Text)
                .ToList();

            if (!selectedMods.Any())
            {
                Log("No mods selected for restore!");
                return;
            }

            int totalFiles = selectedMods.Sum(mod => presetMods[mod].Length);
            if (totalFiles == 0)
            {
                Log("No files to restore!");
                return;
            }

            UpdateStatus("Restoring selected mods...", 0);
            progressBar.Visible = true;

            try
            {
                int processedFiles = 0;
                foreach (var modName in selectedMods)
                {
                    if (presetMods.TryGetValue(modName, out var paths))
                    {
                        foreach (var path in paths)
                        {
                            await RestoreFileAsync(path);
                            processedFiles++;
                            UpdateStatus($"Restoring selected... ({processedFiles}/{totalFiles})",
                                (int)((processedFiles / (double)totalFiles * 100)));
                        }
                    }
                }
                Log("Selected mods restored");
            }
            catch (Exception ex)
            {
                Log($"Error restoring selected mods: {ex.Message}");
            }
            finally
            {
                UpdateStatus("Ready", 0);
            }
        }

        private async Task RestoreBackupsAsync(string backupDir)
        {
            try
            {
                Log($"Starting restore from: {backupDir}");

                var files = await Task.Run(() =>
                    Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories).ToList()
                ).ConfigureAwait(false);

                int total = files.Count;
                int count = 0;

                for (int i = 0; i < total; i++)
                {
                    var file = files[i];
                    try
                    {

                        await Task.Run(async () =>
                        {
                            Log($"Processing backup: {file}");

                            var relativePath = Path.GetRelativePath(backupDir, file)
                                .Replace('\\', '/')
                                .Trim();

                            var isGGPK = relativePath.StartsWith("root/", StringComparison.OrdinalIgnoreCase);
                            var cleanPath = relativePath
                                .Replace("root/", "", StringComparison.OrdinalIgnoreCase)
                                .Replace("bundle/", "", StringComparison.OrdinalIgnoreCase)
                                .TrimStart('/');

                            Log($"Clean path: {cleanPath}");
                            //Log($"Type: {(isGGPK ? "GGPK" : "Bundle")}");

                            var backupContent = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                            Log($"Backup file size: {backupContent.Length} bytes");
                            Log($"Backup file hash: {ComputeSHA256(backupContent)}");

                            if (isGGPK)
                            {
                                if (Ggpk?.Root.TryFindNode(cleanPath, out var node) == true && node is IFileNode fileNode)
                                {
                                    var fileRecord = fileNode.Record;
                                    var oldHash = ComputeSHA256(fileRecord.Read().Span);

                                    await Task.Run(() => fileRecord.Write(backupContent)).ConfigureAwait(false);
                                    Log($"Restored GGPK file: {cleanPath}");

                                    var newHash = ComputeSHA256(fileRecord.Read().Span);
                                    Log($"Restoration {(oldHash == newHash ? "FAILED" : "SUCCESSFUL")}");
                                    Log($"Old hash: {oldHash}");
                                    Log($"New hash: {newHash}");

                                    count++;
                                    UpdateStatus($"Restoring files... ({count}/{total})",
                                        (int)((count / (double)total * 100)));
                                }
                                else
                                {
                                    Log($"GGPK file not found: {cleanPath}");
                                }
                            }
                            else
                            {
                                if (Index == null)
                                {
                                    Log("Bundle index not initialized");
                                    return;
                                }

                                var normalizedPath = cleanPath.ToLowerInvariant();

                                if (Index.TryGetFile(normalizedPath, out var fileRecord))
                                {
                                    var oldHash = ComputeSHA256(fileRecord.Read().Span);

                                    await Task.Run(() =>
                                    {
                                        fileRecord.Write(backupContent);
                                        Index.Save();
                                    }).ConfigureAwait(false);

                                    //Log($"Restored Bundle file: {normalizedPath}");

                                    var newHash = ComputeSHA256(fileRecord.Read().Span);
                                    Log($"Restoration {(oldHash == newHash ? "FAILED" : "SUCCESSFUL")}");
                                    Log($"Old hash: {oldHash}");
                                    Log($"New hash: {newHash}");

                                    count++;
                                }
                                else
                                {
                                    Log($"Bundle file not found: {normalizedPath}");
                                }
                            }
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log($"Error restoring {file}: {ex.Message}");
                    }


                    if (i % 10 == 0 || i == total - 1)
                    {
                        UpdateStatus($"Restoring backups... ({i + 1}/{total})",
                            (int)((i + 1) / (double)total * 100));
                    }
                }

                if (Ggpk != null)
                {
                    await Task.Run(() =>
                    {
                        var stream = Ggpk.UnsafeGetStream();
                        stream.Flush();
                    }).ConfigureAwait(false);

                    Log("GGPK changes flushed");
                }

                Log($"Restoration complete. Total files restored: {count}");
            }
            catch (Exception ex)
            {
                Log($"CRITICAL RESTORE ERROR: {ex}");
            }
        }


        private static string ComputeSHA256(ReadOnlySpan<byte> data)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data.ToArray())).Replace("-", "");
        }

        private void Log(string message)
        {
            var logMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Application.Instance.Invoke(() =>
            {
                logArea.Append(logMessage + "\n", true);
            });
            File.AppendAllText(logFilePath, logMessage + Environment.NewLine);
        }
    }
}