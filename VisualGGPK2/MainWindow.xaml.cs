﻿using LibBundle;
using LibDat2;
using LibGGPK2;
using LibGGPK2.Records;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PixelFormat = System.Windows.Media.PixelFormat;

namespace VisualGGPK2
{
    public partial class MainWindow : Window
    {
        public static string Version;
        public GGPKContainer ggpkContainer;
        /// <summary>
        /// Icon of directory on TreeView
        /// </summary>
        public static readonly BitmapFrame IconDir = BitmapFrame.Create(Assembly.GetExecutingAssembly().GetManifestResourceStream("VisualGGPK2.Resources.dir.ico"));
        /// <summary>
        /// Icon of file on TreeView
        /// </summary>
        public static readonly BitmapFrame IconFile = BitmapFrame.Create(Assembly.GetExecutingAssembly().GetManifestResourceStream("VisualGGPK2.Resources.file.ico"));
        public static readonly ContextMenu TreeMenu = new();
        public static readonly Encoding Unicode = new UnicodeEncoding(false, true);
        public static readonly Encoding UTF8 = new UTF8Encoding(false, false);
        public HttpClient http;
        public readonly bool BundleMode;
		public readonly bool SteamMode;
		protected string FilePath;
        internal static byte SelectedVersion;

        public MainWindow() {
            var args = Environment.GetCommandLineArgs();
            for (var i = 1; i < args.Length; i++)
                switch (args[i].ToLower()) {
                    case "-bundle":
                        BundleMode = true;
                        break;
                    case "-steam":
                        SteamMode = true;
                        break;
                    default:
                        if (FilePath == null && File.Exists(args[i]))
                            FilePath = args[i];
                        break;
                }
            if (BundleMode && SteamMode) {
                MessageBox.Show(this, "BundleMode and SteamMode cannot be both true", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
            InitializeComponent();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e) {
			var currentVersion = Assembly.GetExecutingAssembly().GetName().Version!;
            if (currentVersion.Revision == 0)
                Version = $" (v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build})";
            else
                Version = $" (v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}.{currentVersion.Revision})";
            Title += Version;
            if (SteamMode)
                Title += " (SteamMode)";
            if (BundleMode)
                Title += " (BundleMode)";
#if !DEBUG
            // Version Check
            try {
                var http = new HttpClient {
                    Timeout = TimeSpan.FromSeconds(2)
                };
                http.DefaultRequestHeaders.Add("User-Agent", "VisualGGPK2");
                var json = await http.GetStringAsync("https://api.github.com/repos/aianlinb/LibGGPK2/releases");
                var match = Regex.Match(json, "(?<=\"tag_name\":\"v).*?(?=\")");
                var versionText = $"{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}";
                if (match.Success && match.Value != versionText && MessageBox.Show(this, $"Found a new update on GitHub!\n\nCurrent Version: {versionText}\nLatest Version: {match.Value}\n\nDownload now?", "VisualGGPK2", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
                    Process.Start(new ProcessStartInfo("https://github.com/aianlinb/LibGGPK2/releases") { UseShellExecute = true });
                    Close();
                    return;
                }
                http.Dispose();
            } catch { }
#endif
            // GGPK Selection
            if (FilePath == null) {
                var ofd = new OpenFileDialog {
                    DefaultExt = "ggpk",
                    FileName = SteamMode ? "_.index.bin" : "Content.ggpk",
                    Filter = SteamMode ? "Index Bundle File|*.index.bin" : "GGPK File|*.ggpk"
                };

                var setting = Properties.Settings.Default;
                if (setting.GGPKPath == "") {
                    setting.Upgrade();
                    setting.Save();
                }
                if (setting.GGPKPath == "") {
                    string path;
                    path = Registry.CurrentUser.OpenSubKey(@"Software\GrindingGearGames\Path of Exile")?.GetValue("InstallLocation") as string;
                    if (path != null && File.Exists(path + @"\Content.ggpk")) // Get POE path
                        ofd.InitialDirectory = path.TrimEnd('\\');
                } else if (File.Exists(setting.GGPKPath + @"\Content.ggpk"))
                    ofd.InitialDirectory = setting.GGPKPath;

                if (ofd.ShowDialog() == true) {
                    setting.GGPKPath = Path.GetDirectoryName(FilePath = ofd.FileName);
                    setting.Save();
                } else {
                    Close();
                    return;
                }
            }

            // Initial GGPK
            await Task.Run(() => ggpkContainer = new GGPKContainer(FilePath, BundleMode, SteamMode));
            
            // Initial ContextMenu
            var mi = new MenuItem { Header = "Export" };
            mi.Click += OnExportClicked;
            TreeMenu.Items.Add(mi);
            mi = new MenuItem { Header = "Replace" };
            mi.Click += OnReplaceClicked;
            TreeMenu.Items.Add(mi);
            mi = new MenuItem { Header = "Recovery" };
            mi.Click += OnRecoveryClicked;
            TreeMenu.Items.Add(mi);
            mi = new MenuItem { Header = "Convert dds to png" };
            mi.Click += OnConvertPngClicked;
            TreeMenu.Items.Add(mi);

            var imageMenu = new ContextMenu();
            mi = new MenuItem { Header = "Save as png" };
            mi.Click += OnSavePngClicked;
			imageMenu.Items.Add(mi);
			mi = new MenuItem { Header = "Write png into dds" };
			mi.Click += OnWriteImageClicked;
			imageMenu.Items.Add(mi);
            ImageView.ContextMenu = imageMenu;

            var root = CreateNode(ggpkContainer.rootDirectory);
            Tree.Items.Add(root); // Initial TreeView
            root.IsExpanded = true;

            FilterButton.IsEnabled = true;
            if (!SteamMode)
                AllowGameOpen.IsEnabled = true;

            // Mark the free spaces in data section of dat files
            DatReferenceDataTable.CellStyle = new Style(typeof(DataGridCell));
            DatReferenceDataTable.CellStyle.Setters.Add(new EventSetter(LoadedEvent, new RoutedEventHandler(OnCellLoaded)));

            // Make changes to DatContainer after editing DatTable
            DatTable.CellEditEnding += OnDatTableCellEdit;
            // Make changes to DatContainer after editing DatReferenceDataTable
            DatReferenceDataTable.CellEditEnding += OnDatReferenceDataTableCellEdit;

            TextView.AppendText("\r\n\r\nDone!\r\n");
        }

        /// <summary>
        /// Create a element of the TreeView
        /// </summary>
        public static TreeViewItem CreateNode(RecordTreeNode rtn)
        {
            var tvi = new TreeViewItem { Tag = rtn, Margin = new Thickness(0,1,0,1) };
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            if (rtn is IFileRecord)
                stack.Children.Add(new System.Windows.Controls.Image // Icon
				{
                    Source = IconFile,
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(0, 0, 2, 0)
                });
            else
                stack.Children.Add(new System.Windows.Controls.Image // Icon
				{
                    Source = IconDir,
                    Width = 20,
                    Height = 20,
                    Margin = new Thickness(0, 0, 2, 0)
                });
            stack.Children.Add(new TextBlock { Text = rtn.Name, FontSize = 16 }); // File/Directory Name
            tvi.Header = stack;
            if (rtn is not IFileRecord)
                tvi.Items.Add("Loading . . ."); // Add expand button
            tvi.ContextMenu = TreeMenu;
            return tvi;
        }

        /// <summary>
        /// Directory expanded event
        /// </summary>
        private void OnTreeExpanded(object sender, RoutedEventArgs e)
        {
            var tvi = e.Source as TreeViewItem;
            if (tvi.Items.Count == 1 && tvi.Items[0] is string) // Haven't been expanded yet
            {
                tvi.Items.Clear();
                foreach (var c in ((RecordTreeNode)tvi.Tag).Children)
                    tvi.Items.Add(CreateNode(c));
            }
        }

        /// <summary>
        /// TreeView selected changed event
        /// </summary>
        private void OnTreeSelectedChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (Tree.SelectedItem is TreeViewItem tvi)
            {
                ImageView.Visibility = Visibility.Hidden;
                TextView.Visibility = Visibility.Hidden;
                //OGGView.Visibility = Visibility.Hidden;
                DatView.Visibility = Visibility.Hidden;
                //BK2View.Visibility = Visibility.Hidden;
                //BANKView.Visibility = Visibility.Hidden;
                ButtonSave.Visibility = Visibility.Hidden;
                if (tvi.Tag is RecordTreeNode rtn)
                {
                    TextBoxOffset.Text = "0x" + rtn.Offset.ToString("X");
                    TextBoxSize.Text = rtn.Length.ToString();
                    TextBoxHash.Text = rtn is DirectoryRecord || rtn is FileRecord ? BitConverter.ToString(rtn.Hash).Replace("-", "") : rtn is BundleFileNode bf ? bf.Hash.ToString("X") : ((BundleDirectoryNode)rtn).Hash.ToString("X");
                    TextBoxBundle.Text = "";
                    if (rtn is IFileRecord f)
                    {
                        if (f is FileRecord fr) TextBoxSize.Text = fr.DataLength.ToString();
                        else TextBoxBundle.Text = ((BundleFileNode)f).BundleFileRecord.bundleRecord.Name;
                        switch (f.DataFormat)
                        {
                            case IFileRecord.DataFormats.Image:
                                var b = f.ReadFileContent(ggpkContainer.fileStream);
								Image.Source = BitmapFrame.Create(new MemoryStream(b));
                                Image.Tag = b;
								Image.Width = ImageView.ActualWidth;
                                Image.Height = ImageView.ActualHeight;
                                Canvas.SetLeft(Image, 0);
                                Canvas.SetTop(Image, 0);
                                ImageView.Visibility = Visibility.Visible;
                                break;
                            case IFileRecord.DataFormats.Ascii:
                                TextView.IsReadOnly = false;
                                TextView.Text = UTF8.GetString(f.ReadFileContent(ggpkContainer.fileStream));
                                TextView.Visibility = Visibility.Visible;
                                ButtonSave.Visibility = Visibility.Visible;
                                break;
                            case IFileRecord.DataFormats.Unicode:
                                if (rtn.Parent.Name == "Bundles" || rtn.Name == "minimap_colours.txt")
                                    goto case IFileRecord.DataFormats.Ascii;
                                TextView.IsReadOnly = false;
                                TextView.Text = Unicode.GetString(f.ReadFileContent(ggpkContainer.fileStream)).TrimStart('\xFEFF');
                                TextView.Visibility = Visibility.Visible;
                                ButtonSave.Visibility = Visibility.Visible;
                                break;
                            case IFileRecord.DataFormats.OGG:
                                //TODO
                                //OGGView.Visibility = Visibility.Visible;
                                break;
                            case IFileRecord.DataFormats.Dat:
                                try {
                                    DatView.Visibility = Visibility.Visible;
                                    ShowDatFile(new DatContainer(f.ReadFileContent(ggpkContainer.fileStream), rtn.Name, SchemaMin.IsChecked == true));
                                } catch (Exception ex) {
                                    toMark.Clear();
                                    DatTable.Tag = null;
                                    DatTable.Columns.Clear();
                                    DatTable.ItemsSource = null;
                                    DatReferenceDataTable.Columns.Clear();
                                    DatReferenceDataTable.ItemsSource = null;
                                    MessageBox.Show(this, ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                }
                                break;
                            case IFileRecord.DataFormats.TextureDds:
                                try
                                {
                                    var buffer = f.ReadFileContent(ggpkContainer.fileStream);
                                    buffer = DdsRedirectAndHeaderProcess(buffer, rtn);
									//Image.Source = DdsToBitmap(buffer);
                                    Image.Tag = buffer;
                                    Image.Width = ImageView.ActualWidth;
                                    Image.Height = ImageView.ActualHeight;
                                    Canvas.SetLeft(Image, 0);
                                    Canvas.SetTop(Image, 0);
                                    ImageView.Visibility = Visibility.Visible;
                                } catch (Exception ex) {
                                    TextView.Text = ex.ToString();
                                    TextView.IsReadOnly = true;
                                    TextView.Visibility = Visibility.Visible;
                                }
                                break;
                            case IFileRecord.DataFormats.BK2:
                                //TODO
                                //BK2View.Visibility = Visibility.Visible;
                                break;
                            case IFileRecord.DataFormats.BANK:
                                //TODO
                                //BANKView.Visibility = Visibility.Visible;
                                break;
                        }
                    }
                }
            }
        }

        public static byte[] DdsRedirectAndHeaderProcess(byte[] buffer, RecordTreeNode node) {
			ReadOnlySpan<byte> span = buffer;
			if (node.Name.EndsWith(".header"))
				span = span[16..];
			while (span[0] == '*') {
				var path = UTF8.GetString(span[1..]);
				var f = (IFileRecord)node.ggpkContainer.FindRecord(path, node.ggpkContainer.FakeBundles2);
				span = f.ReadFileContent(node.ggpkContainer.fileStream);
				if (path.EndsWith(".header"))
					span = span[16..];
			}
			return buffer == span ? buffer : span.ToArray();
		}        

        /// <summary>
        /// TreeViewItem MouseDown event
        /// </summary>
        private void OnTreePreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Source is not DependencyObject ui || ui is TreeView) return;

            // Get Clicked TreeViewItem
            while (ui is not TreeViewItem)
                ui = VisualTreeHelper.GetParent(ui);
            var tvi = ui as TreeViewItem;

            if (e.ChangedButton != MouseButton.Left)
                tvi.IsSelected = true; // Select when clicked
            else if (tvi.Tag is DirectoryRecord or BundleDirectoryNode && e.Source is not TreeViewItem) // Is Directory
                tvi.IsExpanded = true; // Expand when left clicked (but not on arrow)
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (!e.Effects.HasFlag(DragDropEffects.Copy)) return; // Drop File/Folder
            Activate();
            var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            string fileName;
            if (dropped.Length != 1 || (fileName = Path.GetFileName(dropped[0])) != ggpkContainer.rootDirectory.Name && !fileName.EndsWith(".zip"))
            {
                MessageBox.Show(this, "You can only drop root folder or a .zip file that contains it", "Replace Faild",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var bkg = new BackgroundDialog();
            if (fileName.EndsWith(".zip"))
                Task.Run(() => {
                    try {
                        var f = ZipFile.OpenRead(dropped[0]);
                        var es = f.Entries;
                        var list = new List<KeyValuePair<IFileRecord, ZipArchiveEntry>>(es.Count);
						try {
                            ggpkContainer.GetFileListFromZip(es, list);
                        } catch (Exception ex) {
                            Dispatcher.Invoke(() => {
                                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                bkg.Close();
                            });
                            return;
                        }
                        var notOk = false;
                        Dispatcher.Invoke(() => {
                            if (notOk = MessageBox.Show(this, $"Replace {list.Count} Files?", "Replace Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                                bkg.Close();
                        });
                        if (notOk) {
                            return;
                        }
                        bkg.ProgressText = "Replacing {0}/" + list.Count.ToString() + " Files . . .";
                        ggpkContainer.Replace(list, bkg.NextProgress);
                        Dispatcher.Invoke(() => {
                            MessageBox.Show(this, "Replaced " + list.Count.ToString() + " Files", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                            bkg.Close();
                        });
                    } catch (Exception ex) {
						App.HandleException(ex);
						Dispatcher.Invoke(bkg.Close);
					}
				});
            else
                Task.Run(() => {
                    try {
                        var list = new Collection<KeyValuePair<IFileRecord, string>>();
                        ggpkContainer.GetFileList(dropped[0], list);
                        var notOk = false;
                        Dispatcher.Invoke(() => {
                            if (notOk = MessageBox.Show(this, $"Replace {list.Count} Files?", "Replace Confirm", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                                bkg.Close();
                        });
                        if (notOk) {
                            return;
                        }
                        bkg.ProgressText = "Replacing {0}/" + list.Count.ToString() + " Files . . .";
                        ggpkContainer.Replace(list, bkg.NextProgress);
                        Dispatcher.Invoke(() => {
                            MessageBox.Show(this, "Replaced " + list.Count.ToString() + " Files", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                            bkg.Close();
                        });
                    } catch (Exception ex) {
						App.HandleException(ex);
						Dispatcher.Invoke(bkg.Close);
					}
				});
            bkg.ShowDialog();
        }

        private void OnExportClicked(object sender, RoutedEventArgs e)
        {
            if ((Tree.SelectedItem as TreeViewItem)?.Tag is RecordTreeNode rtn)
            {
                var sfd = new SaveFileDialog();
                if (rtn is IFileRecord fr)
                {
                    sfd.FileName = rtn.Name;
                    if (sfd.ShowDialog() == true)
                    {
                        File.WriteAllBytes(sfd.FileName, fr.ReadFileContent(ggpkContainer.fileStream));
                        MessageBox.Show(this, "Exported " + rtn.GetPath() + "\nto " + sfd.FileName, "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    sfd.FileName = rtn.Name + ".dir";
                    if (sfd.ShowDialog() == true)
                    {
                        var bkg = new BackgroundDialog();
                        Task.Run(() => {
                            try {
                                var list = new List<KeyValuePair<IFileRecord, string>>();
                                var path = Path.GetDirectoryName(sfd.FileName) + "\\" + rtn.Name;
                                GGPKContainer.RecursiveFileList(rtn, path, list, true);
                                bkg.ProgressText = "Exporting {0}/" + list.Count.ToString() + " Files . . .";
                                list.Sort((a, b) => BundleSortComparer.Instance.Compare(a.Key, b.Key));
                                var failFileCount = 0;
								try {
                                    GGPKContainer.Export(list, bkg.NextProgress);
                                } catch (GGPKContainer.BundleMissingException bex) {
                                    failFileCount = bex.failFiles;
                                    Dispatcher.Invoke(() => MessageBox.Show(this, bex.Message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning));
                                }
                                Dispatcher.Invoke(() => {
                                    MessageBox.Show(this, "Exported " + (list.Count - failFileCount).ToString() + " Files", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                                    bkg.Close();
                                });
                            } catch (Exception ex) {
								App.HandleException(ex);
								Dispatcher.Invoke(bkg.Close);
							}
                        });
                        bkg.ShowDialog();
                    }
                }
            }
        }

        private void OnReplaceClicked(object sender, RoutedEventArgs e)
        {
            if ((Tree.SelectedItem as TreeViewItem)?.Tag is RecordTreeNode rtn)
            {
                if (rtn is IFileRecord fr)
                {
                    var ofd = new OpenFileDialog { FileName = rtn.Name };
                    if (ofd.ShowDialog() == true)
                    {
                        fr.ReplaceContent(File.ReadAllBytes(ofd.FileName));
                        MessageBox.Show(this, "Replaced " + rtn.GetPath() + "\nwith " + ofd.FileName, "Done", MessageBoxButton.OK, MessageBoxImage.Information);
						OnTreeSelectedChanged(null, null);
					}
                }
                else
                {
                    var ofd = new OpenFolderDialog();
                    if (ofd.ShowDialog() == true)
                    {
                        var bkg = new BackgroundDialog();
                        Task.Run(() => {
                            try {
                                var list = new Collection<KeyValuePair<IFileRecord, string>>();
                                GGPKContainer.RecursiveFileList(rtn, ofd.DirectoryPath, list, false);
                                bkg.ProgressText = "Replacing {0}/" + list.Count.ToString() + " Files . . .";
                                ggpkContainer.Replace(list, bkg.NextProgress);
                                Dispatcher.Invoke(() => {
                                    MessageBox.Show(this, "Replaced " + list.Count.ToString() + " Files", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                                    bkg.Close();
                                });
                            } catch (Exception ex) {
								App.HandleException(ex);
								Dispatcher.Invoke(bkg.Close);
							}
						});
                        bkg.ShowDialog();
                    }
                }
            }
        }

        private void OnSaveTextClicked(object sender, RoutedEventArgs e)
        {
            if (Tree.SelectedItem is TreeViewItem tvi && tvi.Tag is IFileRecord fr)
            {
                switch (fr.DataFormat) {
                    case IFileRecord.DataFormats.Ascii:
                        fr.ReplaceContent(UTF8.GetBytes(TextView.Text));
                        break;
                    case IFileRecord.DataFormats.Unicode:
                        if (((RecordTreeNode)fr).GetPath().EndsWith(".amd"))
                            fr.ReplaceContent(Unicode.GetBytes(TextView.Text));
                        else if (((RecordTreeNode)fr).Parent.Name == "Bundles" || ((RecordTreeNode)fr).Name == "minimap_colours.txt")
                            goto case IFileRecord.DataFormats.Ascii;
                        else
                            fr.ReplaceContent(Unicode.GetBytes("\xFEFF" + TextView.Text));
                        break;
                    default:
                        return;
                }
                MessageBox.Show(this, "Saved to " + ((RecordTreeNode)fr).GetPath(), "Done", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnRecoveryClicked(object sender, RoutedEventArgs e) {
            if (new VersionSelector().ShowDialog() != true) return;

            var bkg = new BackgroundDialog();
            var rtn = (RecordTreeNode)((TreeViewItem)Tree.SelectedItem).Tag;
            Task.Run(() => {
                try {
                    string PatchServer = null;
                    var indexUrl = SelectedVersion switch {
                        1 => (PatchServer = GetPatchServer()) + "Bundles2/_.index.bin",
                        2 => (PatchServer = GetPatchServer(true)) + "Bundles2/_.index.bin",
                        3 => "http://poesmoother.eu/owncloud/index.php/s/1VsY1uYOBmfDcMy/download",
                        _ => null
                    };

                    if (SelectedVersion == 3) {
                        var outsideBundles2 = true;
                        var tmp = rtn;
                        do {
                            if (tmp == ggpkContainer.FakeBundles2)
                                outsideBundles2 = false;
                            tmp = tmp.Parent;
                        } while (tmp != null);
                        if (outsideBundles2) {
                            Dispatcher.Invoke(() => {
                                MessageBox.Show(this, "Tencent version currently only support recovering files under \"Bundles2\" directory!", "Unsupported", MessageBoxButton.OK, MessageBoxImage.Error);
                                bkg.Close();
                            });
                            return;
                        }
                    }

                    var l = new List<IFileRecord>();
                    GGPKContainer.RecursiveFileList(rtn, l);
                    bkg.ProgressText = "Recovering {0}/" + l.Count.ToString() + " Files . . .";

                    if (http == null) {
                        http = new() {
                            Timeout = Timeout.InfiniteTimeSpan
                        };
                        http.DefaultRequestHeaders.Add("User-Agent", "VisualGGPK2");
                    }

                    IndexContainer i = null;
                    if (l.Any((ifr) => ifr is BundleFileNode)) {
                        var br = new BinaryReader(new MemoryStream(http.GetByteArrayAsync(indexUrl).Result));
                        i = new IndexContainer(br);
                        br.Close();
                    }

                    foreach (var f in l) {
                        if (f is BundleFileNode bfn) {
                            var bfr = bfn.BundleFileRecord;
                            var newbfr = i.FindFiles[bfr.NameHash];
                            bfr.Offset = newbfr.Offset;
                            bfr.Size = newbfr.Size;
                            if (bfr.BundleIndex != newbfr.BundleIndex) {
                                bfr.BundleIndex = newbfr.BundleIndex;
                                bfr.bundleRecord.Files.Remove(bfr);
                                bfr.bundleRecord = ggpkContainer.Index.Bundles[bfr.BundleIndex];
                                bfr.bundleRecord.Files.Add(bfr);
                            }
                        } else {
                            var fr = f as FileRecord;
                            var path = Regex.Replace(fr.GetPath(), "^ROOT/", "");
                            fr.ReplaceContent(http.GetByteArrayAsync(PatchServer + path).Result);
                        }
                        bkg.NextProgress();
                    }

                    if (i != null)
                        if (SteamMode)
                            ggpkContainer.Index.Save("_.index.bin");
                        else
                            ggpkContainer.IndexRecord.ReplaceContent(ggpkContainer.Index.Save());
                    Dispatcher.Invoke(() => {
                        MessageBox.Show(this, "Recoveried " + l.Count.ToString() + " Files", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                        bkg.Close();
                        OnTreeSelectedChanged(null, null);
                    });
                } catch (Exception ex) {
					App.HandleException(ex);
					Dispatcher.Invoke(bkg.Close);
				}
			});
            bkg.ShowDialog();
		}

		private static string GetPatchServer(bool garena = false) {
            var tcp = new TcpClient() { NoDelay = true };
            tcp.Connect(Dns.GetHostAddresses(garena ? "login.tw.pathofexile.com" : "us.login.pathofexile.com"), garena ? 12999 : 12995);
            var b = new byte[256];
            tcp.Client.Send(new byte[] { 1, 4 });
            tcp.Client.Receive(b);
            tcp.Close();
            return Encoding.Unicode.GetString(b, 35, b[34] * 2);
        }

        private void OnConvertPngClicked(object sender, RoutedEventArgs e) {
            if ((Tree.SelectedItem as TreeViewItem)?.Tag is not RecordTreeNode rtn) {
				MessageBox.Show(this, "You must select a node first", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
				return;
			}
            if (rtn is not IFileRecord) { // directory
                var sfd = new SaveFileDialog { FileName = rtn.Name + ".dir", Filter= "*.*|*.*" };
                if (sfd.ShowDialog() == true) {
                    var bkg = new BackgroundDialog();
                    Task.Run(() => {
						try {
							var list = new List<KeyValuePair<IFileRecord, string>>();
							var path = Path.GetDirectoryName(sfd.FileName) + "\\" + rtn.Name;
							GGPKContainer.RecursiveFileList(rtn, path, list, true, ".dds$");
							bkg.ProgressText = "Converting {0}/" + list.Count.ToString() + " Files . . .";
							list.Sort((x, y) => BundleSortComparer.Instance.Compare(x.Key, y.Key));
							var failFileCount = 0;
							try {
								var fail = BatchConvertPng(list, bkg.NextProgress);
								if (fail < 0) {
									Dispatcher.Invoke(() => bkg.Close());
									return;
								}
                                failFileCount += fail;
							} catch (GGPKContainer.BundleMissingException bex) {
								failFileCount = bex.failFiles;
								Dispatcher.Invoke(() => MessageBox.Show(this, bex.Message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning));
							}
							Dispatcher.Invoke(() => {
								if (failFileCount > 0)
									MessageBox.Show(this, "Converted " + (list.Count - failFileCount) + " files\r\n" + failFileCount + "files failed", "Done", MessageBoxButton.OK, MessageBoxImage.Warning);
								else
									MessageBox.Show(this, "Converted " + list.Count + " Files", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
								bkg.Close();
							});
						} catch (Exception ex) {
							App.HandleException(ex);
							Dispatcher.Invoke(bkg.Close);
						}
					});
                    bkg.ShowDialog();
                }
            } else { // file
				if (ImageView.Visibility != Visibility.Visible)
					MessageBox.Show(this, "Selected file is not a dds file", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                else {
                    var buffer = (byte[])Image.Tag;

					string name;
                    if (rtn.Name.EndsWith(".dds"))
                        name = rtn.Name[..^4] + ".png";
					else if (rtn.Name.EndsWith(".dds.header"))
						name = rtn.Name + ".png";
					else {
						MessageBox.Show(this, "Selected file is not a dds file", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
					}
					
					var sfd = new SaveFileDialog { FileName = name, Filter = "*.png|*.png" };
                    if (sfd.ShowDialog() == true) {
                        //SaveImageSource(DdsToBitmap(buffer), sfd.FileName);
						MessageBox.Show(this, "Saved " + sfd.FileName, "Done", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
				}
			}
		}

		private void OnSavePngClicked(object sender, RoutedEventArgs e) {
			if ((Tree.SelectedItem as TreeViewItem)?.Tag is not RecordTreeNode rtn) {
				MessageBox.Show(this, "You must select a image file first", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
				return;
			}
			var name = rtn.Name.EndsWith(".dds") ? rtn.Name[..^4] + ".png" : rtn.Name.EndsWith(".png") ? rtn.Name : rtn.Name + ".png";
            var sfd = new SaveFileDialog { FileName = name, Filter = "*.png|*.png" };
            if (sfd.ShowDialog() == true) {
                //DdsToPngFile(DdsToPng((byte[])Image.Tag), sfd.FileName);
                if (rtn.Name.EndsWith(".png"))
                    File.WriteAllBytes(sfd.FileName, (byte[])Image.Tag);
                else
                    SaveImageSource((BitmapSource)Image.Source, sfd.FileName);
				MessageBox.Show(this, "Saved " + sfd.FileName, "Done", MessageBoxButton.OK, MessageBoxImage.Information);
			}
		}
		
		public static void SaveImageSource(BitmapSource source, string path) {
			var fs = File.Create(path);
			var png = new PngBitmapEncoder();
			png.Frames.Add(source is BitmapFrame bf ? bf : BitmapFrame.Create(source));
			png.Save(fs);
			fs.Close();
		}

		private int BatchConvertPng(ICollection<KeyValuePair<IFileRecord, string>> list, Action ProgressStep = null) {
			var regex = new Regex(".dds$");
            LibBundle.Records.BundleRecord br = null;
            MemoryStream ms = null;
            var failBundles = 0;
            var failFiles = 0;
            var fail = 0;
            var done = 0;
			var semaphore = new Semaphore(Environment.ProcessorCount - 1, Environment.ProcessorCount - 1);
            COMException COM = null;
            string errorPath = null;

			foreach (var (record, path) in list) {
                if (COM != null) {
					if (MessageBox.Show(this, $"Error when processing file: {errorPath}\r\n\r\n{COM}\r\nIgnore and continue?", "Error", MessageBoxButton.YesNo, MessageBoxImage.Error) != MessageBoxResult.Yes) {
						semaphore.Dispose();
						semaphore = null;
						return -1;
					}
                    COM = null;
				}
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (record is BundleFileNode bfn) {
                    if (br != bfn.BundleFileRecord.bundleRecord) {
                        ms?.Close();
                        br = bfn.BundleFileRecord.bundleRecord;
                        br.Read(bfn.ggpkContainer.Reader, bfn.ggpkContainer.RecordOfBundle(br)?.DataBegin);
                        ms = br.Bundle?.Read(bfn.ggpkContainer.Reader);
                        if (ms == null)
                            ++failBundles;
                    }
                    if (ms == null)
                        ++failFiles;
                    else {
						if (COM != null) {
							if (MessageBox.Show(this, $"Error when processing file: {errorPath}\r\n\r\n{COM}\r\nIgnore and continue?", "Error", MessageBoxButton.YesNo, MessageBoxImage.Error) != MessageBoxResult.Yes) {
								semaphore.Dispose();
								semaphore = null;
								return -1;
							}
							COM = null;
						}
						semaphore.WaitOne();
						var b = bfn.BatchReadFileContent(ms);
						Task.Run(() => {
							try {
								b = DdsRedirectAndHeaderProcess(b, bfn);
								//SaveImageSource(DdsToBitmap(b), path[..^4] + ".png");
								Interlocked.Increment(ref done);
								ProgressStep?.Invoke();
                                semaphore?.Release();
							} catch (COMException ex) {
								Interlocked.Increment(ref fail);
								if (COM == null) {
									COM = ex;
									errorPath = bfn.GetPath();
									semaphore?.Release();
								}
							}
						});
					}
                } else {
					if (COM != null) {
						if (MessageBox.Show(this, $"Error when processing file: {errorPath}\r\n\r\n{COM}\r\nIgnore and continue?", "Error", MessageBoxButton.YesNo, MessageBoxImage.Error) != MessageBoxResult.Yes) {
							semaphore.Dispose();
							semaphore = null;
							return -1;
						}
						COM = null;
					}
					semaphore.WaitOne();
					var b = record.ReadFileContent();
					Task.Run(() => {
						try {
							b = DdsRedirectAndHeaderProcess(b, (RecordTreeNode)record);
							//SaveImageSource(DdsToBitmap(b), path[..^4] + ".png");
							Interlocked.Increment(ref done);
							ProgressStep?.Invoke();
							if (COM == null)
								semaphore.Release();
						} catch (COMException ex) {
							Interlocked.Increment(ref fail);
							if (COM == null) {
								COM = ex;
                                errorPath = ((RecordTreeNode)record).GetPath();
								semaphore?.Release();
							}
						}
					});
				}
			}
			semaphore.WaitOne();
			while (done < list.Count)
                Thread.Sleep(500);
            semaphore.Dispose();
			if (failBundles != 0 || failFiles != 0)
                throw new GGPKContainer.BundleMissingException(failBundles, failFiles);
            return fail;
        }

		private unsafe void OnWriteImageClicked(object sender, RoutedEventArgs e) {
			if ((Tree.SelectedItem as TreeViewItem)?.Tag is not IFileRecord fr || Image.Visibility != Visibility.Visible) {
				MessageBox.Show(this, "You must select a dds file first", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
				return;
			}
            var rtn = (RecordTreeNode)fr;
			if (!rtn.Name.EndsWith(".dds")) {
				MessageBox.Show(this, "Selected file is not a dds file", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
				return;
            }

			var ofd = new OpenFileDialog { FileName = rtn.Name, Filter = "Image File|*.png;*.jpg;*.bmp;*.jpeg;*.gif;*.tiff;*.ico|*.*|*.*" };
            if (ofd.ShowDialog() != true)
                return;
			if (MessageBox.Show(this, "The image will directly write into dds in ggpk\r\nThis is an experimental feature and may not work as expected!\r\n\r\nClick OK to continue", "Error", MessageBoxButton.OK, MessageBoxImage.Warning) != MessageBoxResult.OK)
				return;

            var bitmap = (Bitmap)System.Drawing.Image.FromFile(ofd.FileName);
            uint fourcc; // Get origin dds DXGI_FORMAT
            fixed (byte* p = (byte[])Image.Tag)
                fourcc = *(uint*)(p + 84);
            //var blob = BitmapToDdsFile(bitmap);
			//fr.ReplaceContent(new ReadOnlySpan<byte>(blob.Pointer, blob.Length).ToArray());

			MessageBox.Show(this, "Wrote " + ofd.FileName + "\r\ninto " + rtn.GetPath(), "Done", MessageBoxButton.OK, MessageBoxImage.Information);
			OnTreeSelectedChanged(null, null);
		}

		private void FilterButton_Click(object sender, RoutedEventArgs e) {
            Tree.Items.Clear();
            ggpkContainer.FakeBundles2.Children.Clear();
            foreach (var f in ggpkContainer.Index.Files)
                if (RegexCheckBox.IsChecked.Value && Regex.IsMatch(f.path, FilterBox.Text) || !RegexCheckBox.IsChecked.Value && f.path.Contains(FilterBox.Text)) ggpkContainer.BuildBundleTree(f, ggpkContainer.FakeBundles2);
            var root = CreateNode(ggpkContainer.rootDirectory);
            Tree.Items.Add(root);
            root.IsSelected = true; // Clear view
            root.IsExpanded = true;
        }

		private void FilterBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key != Key.Enter || !FilterButton.IsEnabled) return;
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(FilterBox), null);
            Keyboard.ClearFocus();
            FilterButton_Click(null, null);
            e.Handled = true;
        }

        private async void AllowGameOpen_Click(object sender, RoutedEventArgs e) {
            ggpkContainer.fileStream.Close();
            var fi = new FileInfo(FilePath);
            var t = fi.LastWriteTimeUtc;
            var l = fi.Length;
        loop:
			try {
                MessageBox.Show(this, "GGPK file is now closed, you can open the game!\nClose the game and click OK to reopen the GGPK file and return to VisualGGPK2", "Released File Handle", MessageBoxButton.OK, MessageBoxImage.Information);
                fi = new FileInfo(FilePath);
                if (fi.LastWriteTimeUtc != t || fi.Length != l) {
                    MessageBox.Show(this, "The Content.ggpk has been modified, Now it's going to be reloaded", "GGPK Changed", MessageBoxButton.OK, MessageBoxImage.Warning);

                    Tree.Items.Clear();
                    TextView.Text = "Loading . . .";
                    TextView.Visibility = Visibility.Visible;
                    FilterButton.IsEnabled = false;
                    AllowGameOpen.IsEnabled = false;

                    // Initial GGPK
                    await Task.Run(() => ggpkContainer = new GGPKContainer(FilePath, BundleMode, SteamMode));

                    var root = CreateNode(ggpkContainer.rootDirectory);
                    Tree.Items.Add(root); // Initial TreeView
                    root.IsExpanded = true;

                    FilterButton.IsEnabled = true;
                    if (!SteamMode)
                        AllowGameOpen.IsEnabled = true;

                    TextView.AppendText("\r\n\r\nDone!\r\n");
                } else {
                    ggpkContainer.fileStream = File.Open(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    ggpkContainer.Reader = new(ggpkContainer.fileStream);
                    ggpkContainer.Writer = new(ggpkContainer.fileStream);
                }
            } catch (IOException) {
                MessageBox.Show(this, "Cannot access the file, make sure you have closed the game!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                goto loop;
            }
        }

        private void ImageView_MouseWheel(object sender, MouseWheelEventArgs e) {
            var p = e.GetPosition(ImageView);
            var x = Canvas.GetLeft(Image);
            var y = Canvas.GetTop(Image);
            if (e.Delta > 0) {
                Canvas.SetLeft(Image, x - (p.X - x) * 0.2);
                Canvas.SetTop(Image, y - (p.Y - y) * 0.2);
                Image.Width *= 1.2;
                Image.Height *= 1.2;
            } else {
                Canvas.SetLeft(Image, x + (p.X - x) / 6);
                Canvas.SetTop(Image, y + (p.Y - y) / 6);
                Image.Width /= 1.2;
                Image.Height /= 1.2;
            }
        }
    }
}