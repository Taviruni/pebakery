﻿/*
    Copyright (C) 2016-2017 Hajin Jang
    Licensed under GPL 3.0
 
    PEBakery is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using PEBakery.Helper;
using PEBakery.Lib;
using PEBakery.Core;
using MahApps.Metro.IconPacks;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SQLite.Net;
using System.Text;
using PEBakery.Exceptions;
using System.Runtime.CompilerServices;

namespace PEBakery.WPF
{
    #region MainWindow
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : Window
    {
        private ProjectCollection projects;
        public ProjectCollection Projects { get => projects; }

        private string baseDir;
        public string BaseDir { get => baseDir; }

        private BackgroundWorker loadWorker = new BackgroundWorker();
        private BackgroundWorker refreshWorker = new BackgroundWorker();

        private TreeViewModel currentTree;
        public TreeViewModel CurrentTree { get => currentTree; }

        private Logger logger;
        public Logger Logger { get => logger; }
        private PluginCache pluginCache;

        const int MaxDpiScale = 4;
        private int allPluginCount = 0;
        private readonly string settingFile;
        private SettingViewModel setting;
        public SettingViewModel Setting { get => setting; }

        private readonly string LogSeperator = "--------------------------------------------------------------------------------";
        public MainViewModel Model;

        public MainWindow()
        {
            InitializeComponent();
            this.Model = this.DataContext as MainViewModel;

            string[] args = App.Args;
            if (int.TryParse(Properties.Resources.IntegerVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out App.Version) == false)
            {
                Console.WriteLine("Cannot determine version");
                Application.Current.Shutdown(1);
            }  

            // string argBaseDir = FileHelper.GetProgramAbsolutePath();
            string argBaseDir = Environment.CurrentDirectory;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "/basedir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        argBaseDir = System.IO.Path.GetFullPath(args[i + 1]);
                        Environment.CurrentDirectory = argBaseDir;
                    }
                    else
                    {
                        Console.WriteLine("\'/basedir\' must be used with path\r\n");
                    }
                }
                else if (string.Equals(args[i], "/?", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "/help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "/h", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Sorry, help message not implemented\r\n");
                }
            }

            this.baseDir = argBaseDir;

            this.settingFile = Path.Combine(argBaseDir, "PEBakery.ini");
            App.Setting = this.setting = new SettingViewModel(settingFile);
            Logger.DebugLevel = setting.Log_DebugLevel;

            string logDBFile = System.IO.Path.Combine(baseDir, "PEBakeryLog.db");
            try
            {
                App.Logger = logger = new Logger(logDBFile);
                logger.System_Write(new LogInfo(LogState.Info, $"PEBakery launched"));
            }
            catch (SQLiteException e)
            { // Update failure
                string msg = $"SQLite Error : {e.Message}\r\n\r\nLog database is corrupted.\r\nPlease delete PEBakeryLog.db and restart.";
                MessageBox.Show(msg, "SQLite Error!", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown(1);
            }
            this.setting.LogDB = logger.DB;

            // If plugin cache is enabled, generate cache after 5 seconds
            if (setting.Plugin_EnableCache)
            {
                string cacheDBFile = System.IO.Path.Combine(baseDir, "PEBakeryCache.db");
                try
                {
                    this.pluginCache = new PluginCache(cacheDBFile);
                    logger.System_Write(new LogInfo(LogState.Info, $"PluginCache enabled, {pluginCache.Table<DB_PluginCache>().Count()} cached plugin found"));
                }
                catch (SQLiteException e)
                { // Update failure
                    string msg = $"SQLite Error : {e.Message}\r\n\r\nCache database is corrupted.\r\nPlease delete PEBakeryCache.db and restart.";
                    MessageBox.Show(msg, "SQLite Error!", MessageBoxButton.OK, MessageBoxImage.Error);
                    Application.Current.Shutdown(1);
                }

                this.setting.CacheDB = pluginCache;
            }
            else
            {
                logger.System_Write(new LogInfo(LogState.Info, $"PluginCache disabled"));
            }

            StartLoadWorker();
        }

        private void StartLoadWorker()
        {
            Stopwatch watch = Stopwatch.StartNew();

            Image image = new Image()
            {
                UseLayoutRounding = true,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Source = ImageHelper.ToBitmapImage(Properties.Resources.DonutPng),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            PluginLogo.Content = image;
            Model.PluginTitleText = "Welcome to PEBakery!";
            Model.PluginDescriptionText = "PEBakery loading...";
            logger.System_Write(new LogInfo(LogState.Info, $@"Loading from [{baseDir}]"));
            MainCanvas.Children.Clear();

            int stage2LinksCount = 0;
            int loadedPluginCount = 0;
            int stage1CachedCount = 0;
            int stage2LoadedCount = 0;
            int stage2CachedCount = 0;

            Model.BottomProgressBarMinimum = 0;
            Model.BottomProgressBarMaximum = 100;
            Model.BottomProgressBarValue = 0;
            Model.ProgressRingActive = true;
            Model.SwitchStatusProgressBar = false; // Show Progress Bar
            loadWorker = new BackgroundWorker();

            loadWorker.DoWork += (object sender, DoWorkEventArgs e) =>
            {
                string baseDir = (string)e.Argument;
                BackgroundWorker worker = sender as BackgroundWorker;


                // Init ProjectCollection
                if (setting.Plugin_EnableCache) // Use PluginCache - Fast speed, more memory
                    projects = new ProjectCollection(baseDir, pluginCache);
                else  // Do not use PluginCache - Slow speed, less memory
                    projects = new ProjectCollection(baseDir, null);

                allPluginCount = projects.PrepareLoad(out stage2LinksCount);
                Dispatcher.Invoke(() => { Model.BottomProgressBarMaximum = allPluginCount + stage2LinksCount; });

                // Let's load plugins parallelly
                projects.Load(worker);
                setting.UpdateProjectList();

                // Populate TreeView
                Dispatcher.Invoke(() =>
                {
                    foreach (Project project in projects.Projects)
                    {
                        List<Node<Plugin>> plugins = project.VisiblePlugins.Root;
                        RecursivePopulateMainTreeView(plugins, Model.Tree, Model.Tree);
                    };
                    int pIdx = setting.Project_DefaultIndex;
                    currentTree = Model.Tree.Child[pIdx];
                    currentTree.IsExpanded = true;
                    if (projects[pIdx] != null)
                        DrawPlugin(projects[pIdx].MainPlugin);
                });
            };
            loadWorker.WorkerReportsProgress = true;
            loadWorker.ProgressChanged += (object sender, ProgressChangedEventArgs e) =>
            {
                Interlocked.Increment(ref loadedPluginCount);
                Model.BottomProgressBarValue = loadedPluginCount;
                string msg = string.Empty;
                switch (e.ProgressPercentage)
                {
                    case 0:  // Stage 1
                        if (e.UserState == null)
                            msg = $"Error";
                        else
                            msg = $"{e.UserState}";
                        break;
                    case 1:  // Stage 1, Cached
                        Interlocked.Increment(ref stage1CachedCount);
                        if (e.UserState == null)
                            msg = $"Cached - Error";
                        else
                            msg = $"Cached - {e.UserState}";
                        break;
                    case 2:  // Stage 2
                        Interlocked.Increment(ref stage2LoadedCount); 
                        if (e.UserState == null)
                            msg = $"Error";
                        else
                            msg = $"{e.UserState}";
                        break;
                    case 3:  // Stage 2, Cached
                        Interlocked.Increment(ref stage2LoadedCount);
                        Interlocked.Increment(ref stage2CachedCount);
                        if (e.UserState == null)
                            msg = $"Cached - Error";
                        else
                            msg = $"Cached - {e.UserState}";
                        break;
                }
                int stage = e.ProgressPercentage / 2 + 1;
                if (stage == 1)
                    msg = $"Stage {stage} ({loadedPluginCount} / {allPluginCount}) \r\n{msg}";
                else
                    msg = $"Stage {stage} ({stage2LoadedCount} / {stage2LinksCount}) \r\n{msg}";

                Model.PluginDescriptionText = $"PEBakery loading...\r\n{msg}";
            };
            loadWorker.RunWorkerCompleted += (object sender, RunWorkerCompletedEventArgs e) =>
            {
                StringBuilder b = new StringBuilder();
                b.Append("Projects [");
                List<Project> projList = projects.Projects;
                for (int i = 0; i < projList.Count; i++)
                {
                    b.Append(projList[i].ProjectName);
                    if (i + 1 < projList.Count)
                        b.Append(", ");
                }
                b.Append("] loaded");
                logger.System_Write(new LogInfo(LogState.Info, b.ToString()));

                watch.Stop();
                double t = watch.Elapsed.TotalMilliseconds / 1000.0;
                string msg;
                if (setting.Plugin_EnableCache)
                {
                    double cachePercent = (double)(stage1CachedCount + stage2CachedCount) * 100 / (allPluginCount + stage2LinksCount);
                    msg = $"{allPluginCount} plugins loaded ({cachePercent:0.#}% cached), took {t:0.###}sec";
                    Model.StatusBarText = msg;
                }
                else
                {
                    msg = $"{allPluginCount} plugins loaded, took {t:hh\\:mm\\:ss}";
                    Model.StatusBarText = msg;
                }
                Model.ProgressRingActive = false;
                Model.SwitchStatusProgressBar = true; // Show Status Bar
                
                logger.System_Write(new LogInfo(LogState.Info, msg));
                logger.System_Write(LogSeperator);

                // If plugin cache is enabled, generate cache.
                if (setting.Plugin_EnableCache)
                    StartCacheWorker();
            };
            loadWorker.RunWorkerAsync(baseDir);
        }

        private void StartCacheWorker()
        {
            // TODO: Prevent DB Corruption due to sudden exit
            if (PluginCache.dbLock == 0)
            {
                Interlocked.Increment(ref PluginCache.dbLock);
                try
                {
                    Stopwatch watch = new Stopwatch();
                    BackgroundWorker cacheWorker = new BackgroundWorker();

                    Model.ProgressRingActive = true;
                    int updatedCount = 0;
                    int cachedCount = 0;
                    cacheWorker.DoWork += (object sender, DoWorkEventArgs e) =>
                    {
                        BackgroundWorker worker = sender as BackgroundWorker;

                        watch = Stopwatch.StartNew();
                        pluginCache.CachePlugins(projects, worker);
                    };

                    cacheWorker.WorkerReportsProgress = true;
                    cacheWorker.ProgressChanged += (object sender, ProgressChangedEventArgs e) =>
                    {
                        Interlocked.Increment(ref cachedCount);
                        if (e.ProgressPercentage == 1)
                            Interlocked.Increment(ref updatedCount);
                    };
                    cacheWorker.RunWorkerCompleted += (object sender, RunWorkerCompletedEventArgs e) =>
                    {
                        watch.Stop();

                        double cachePercent = (double)updatedCount * 100 / allPluginCount;

                        double t = watch.Elapsed.TotalMilliseconds / 1000.0;
                        string msg = $"{allPluginCount} plugins cached ({cachePercent:0.#}% updated), took {t:0.###}sec";
                        logger.System_Write(new LogInfo(LogState.Info, msg));
                        logger.System_Write(LogSeperator);

                        Model.ProgressRingActive = false;
                    };
                    cacheWorker.RunWorkerAsync();
                }
                finally
                {
                    Interlocked.Decrement(ref PluginCache.dbLock);
                }
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (loadWorker.IsBusy == false)
            {
                (MainTreeView.DataContext as TreeViewModel).Child.Clear();

                StartLoadWorker();
            }
        }

        private void RecursivePopulateMainTreeView(List<Node<Plugin>> plugins, TreeViewModel treeRoot, TreeViewModel treeParent)
        {
            foreach (Node<Plugin> node in plugins)
            {
                Plugin p = node.Data;

                TreeViewModel item = new TreeViewModel(treeRoot, treeParent);
                treeParent.Child.Add(item);
                item.Node = node;

                if (p.Type == PluginType.Directory)
                {
                    item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.Folder, 0));
                }
                else if (p.Type == PluginType.Plugin)
                {
                    if (p.Level == Project.MainLevel)
                        item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.Settings, 0));
                    else if (p.Mandatory)
                        item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.LockOutline, 0));
                    else
                        item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.File, 0));
                }
                else if (p.Type == PluginType.Link)
                {
                    item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.OpenInNew, 0));
                }

                if (0 < node.Child.Count)
                    RecursivePopulateMainTreeView(node.Child, treeRoot, item);
            }
        }

        private void MainTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var tree = sender as TreeView;

            if (tree.SelectedItem is TreeViewModel)
            {
                TreeViewModel item = currentTree = tree.SelectedItem as TreeViewModel;

                Dispatcher.Invoke(() =>
                {
                    Stopwatch watch = new Stopwatch();
                    watch.Start();
                    DrawPlugin(item.Node.Data);
                    watch.Stop();
                    double sec = watch.Elapsed.TotalSeconds;
                    Model.StatusBarText = $"{Path.GetFileName(currentTree.Node.Data.ShortPath)} rendered. Took {sec:0.000}sec";
                });
            }
        }

        public void DrawPlugin(Plugin p)
        {
            Stopwatch watch = new Stopwatch();
            double size = PluginLogo.ActualWidth * MaxDpiScale;
            if (p.Type == PluginType.Directory)
                PluginLogo.Content = ImageHelper.GetMaterialIcon(PackIconMaterialKind.Folder, 0);
            else
            {
                try
                {
                    MemoryStream mem = EncodedFile.ExtractLogo(p, out ImageType type);
                    if (type == ImageType.Svg)
                    {
                        Image image = new Image()
                        {
                            Source = ImageHelper.SvgToBitmapImage(mem, size, size),
                            Stretch = Stretch.Uniform
                        };
                        PluginLogo.Content = image;
                    }
                    else
                    {
                        Image image = new Image();
                        BitmapImage bitmap = ImageHelper.ImageToBitmapImage(mem);
                        image.StretchDirection = StretchDirection.DownOnly;
                        image.Stretch = Stretch.Uniform;
                        image.UseLayoutRounding = true; // Must to prevent blurry image rendering
                        image.Source = bitmap;

                        Grid grid = new Grid();
                        grid.Children.Add(image);

                        PluginLogo.Content = grid;
                    }
                }
                catch
                { // No logo file - use default
                    if (p.Type == PluginType.Plugin)
                        PluginLogo.Content = ImageHelper.GetMaterialIcon(PackIconMaterialKind.FileDocument, 0);
                    else if (p.Type == PluginType.Link)
                        PluginLogo.Content = ImageHelper.GetMaterialIcon(PackIconMaterialKind.OpenInNew, 0);
                }
            }

            MainCanvas.Children.Clear();
            if (p.Type == PluginType.Directory)
            {
                Model.PluginTitleText = StringEscaper.Unescape(p.Title);
                Model.PluginDescriptionText = string.Empty;
                Model.PluginVersionText = string.Empty;
                Model.PluginAuthorText = string.Empty;
            }
            else
            {
                Model.PluginTitleText = StringEscaper.Unescape(p.Title);
                Model.PluginDescriptionText = StringEscaper.Unescape(p.Description);
                Model.PluginVersionText = $"v{p.Version}";
                Model.PluginAuthorText = p.Author;

                double scaleFactor = setting.Interface_ScaleFactor / 100;
                ScaleTransform scale = new ScaleTransform(scaleFactor, scaleFactor);
                UIRenderer render = new UIRenderer(MainCanvas, this, p, logger, scaleFactor);
                MainCanvas.LayoutTransform = scale;
                render.Render();
            }
        }

        private void StartRefreshWorker()
        {
            if (currentTree == null)
                return;

            if (refreshWorker.IsBusy)
                return;

            Stopwatch watch = new Stopwatch();

            Model.ProgressRingActive = true;
            refreshWorker = new BackgroundWorker();
            refreshWorker.DoWork += (object sender, DoWorkEventArgs e) =>
            {
                watch.Start();
                Plugin p = currentTree.Node.Data.Project.RefreshPlugin(currentTree.Node.Data);
                if (p != null)
                {
                    currentTree.Node.Data = p;
                    Dispatcher.Invoke(() => 
                    {
                        currentTree.Node.Data = p;
                        DrawPlugin(currentTree.Node.Data);
                    });
                }
            };
            refreshWorker.RunWorkerCompleted += (object sender, RunWorkerCompletedEventArgs e) =>
            {
                Model.ProgressRingActive = false;
                watch.Stop();
                double sec = watch.Elapsed.TotalSeconds;
                string msg = $"{Path.GetFileName(currentTree.Node.Data.ShortPath)} reloaded. Took {sec:0.000}sec";
                Model.StatusBarText = msg;
            };
            refreshWorker.RunWorkerAsync();
        }

        private void MainTreeView_Loaded(object sender, RoutedEventArgs e)
        {
            Window window = Window.GetWindow(this);
            window.KeyDown += MainTreeView_KeyDown;
        }

        /// <summary>
        /// Used to ensure pressing 'Space' to toggle TreeView's checkbox.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainTreeView_KeyDown(object sender, KeyEventArgs e)
        {
            // Window window = sender as Window;
            base.OnKeyDown(e);

            if (e.Key == Key.Space)
            {
                if (Keyboard.FocusedElement is FrameworkElement focusedElement)
                {
                    if (focusedElement.DataContext is TreeViewModel node)
                    {
                        if (node.Checked == true)
                            node.Checked = false;
                        else if (node.Checked == false)
                            node.Checked = true;
                        e.Handled = true;
                    }
                }
            }
        }

        private void BuildButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle Normal View with Build View
            if (Model.SwitchNormalBuildInterface)
            {
                Model.SwitchNormalBuildInterface = false;
            }
            else
            {
                Model.SwitchNormalBuildInterface = true;
            }
        }

        private void SettingButton_Click(object sender, RoutedEventArgs e)
        {
            double old_Interface_ScaleFactor = setting.Interface_ScaleFactor;
            bool old_Plugin_EnableCache = setting.Plugin_EnableCache;

            SettingWindow dialog = new SettingWindow(setting);
            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                // Scale Factor
                double newScaleFactor = setting.Interface_ScaleFactor;
                if (double.Epsilon < Math.Abs(newScaleFactor - old_Interface_ScaleFactor)) // Not Equal
                    DrawPlugin(currentTree.Node.Data);

                // PluginCache
                if (old_Plugin_EnableCache == false && setting.Plugin_EnableCache)
                    StartCacheWorker();

                // DebugLevel
                Logger.DebugLevel = setting.Log_DebugLevel;
            }
        }

        private void PluginRunButton_Click(object sender, RoutedEventArgs e)
        {
            Plugin p = currentTree.Node.Data;
            if (p.Sections.ContainsKey("Process"))
            {
                SectionAddress addr = new SectionAddress(p, p.Sections["Process"]);
                Engine.RunOneSectionInUI(addr, $"{p.Title} - Run");
            }
            else
            {
                string msg = $"Cannot run [{p.Title}]!\r\nPlease implement section [Process]";
                Logger.System_Write(new LogInfo(LogState.Error, msg));
                MessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PluginEditButton_Click(object sender, RoutedEventArgs e)
        {
            ProcessStartInfo procInfo = new ProcessStartInfo()
            {
                Verb = "open",
                FileName = currentTree.Node.Data.FullPath,
                UseShellExecute = true
            };
            Process.Start(procInfo);
        }

        private void PluginRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            StartRefreshWorker();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            logger.DB.Close();
            if (pluginCache != null)
                pluginCache.WaitClose();
        }

        private void LogButton_Click(object sender, RoutedEventArgs e)
        {
            LogWindow dialog = new LogWindow();
            dialog.Show();
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Not Implemented", "Sorry", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Not Implemented", "Sorry", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void ToolBoxButton_Click(object sender, RoutedEventArgs e)
        {

        }
    }
    #endregion

    #region MainViewModel
    public class MainViewModel : INotifyPropertyChanged
    {
        public MainViewModel()
        {
            this.tree = new TreeViewModel(null, null);
        }

        private bool progressRingActive = true;
        public bool ProgressRingActive
        {
            get => progressRingActive;
            set
            {
                progressRingActive = value;
                OnPropertyUpdate("ProgressRingActive");
            }
        }

        private string pluginTitleText = "Welcome to PEBakery!";
        public string PluginTitleText
        {
            get => pluginTitleText;
            set
            {
                pluginTitleText = value;
                OnPropertyUpdate("PluginTitleText");
            }
        }

        private string pluginAuthorText = "Hajin Jang";
        public string PluginAuthorText
        {
            get => pluginAuthorText;
            set
            {
                pluginAuthorText = value;
                OnPropertyUpdate("PluginAuthorText");
            }
        }

        private string pluginVersionText = "v0.1";
        public string PluginVersionText
        {
            get => pluginVersionText;
            set
            {
                pluginVersionText = value;
                OnPropertyUpdate("PluginVersionText");
            }
        }

        private string pluginDescriptionText = "PEBakery is now loading, please wait...";
        public string PluginDescriptionText
        {
            get => pluginDescriptionText;
            set
            {
                pluginDescriptionText = value;
                OnPropertyUpdate("PluginDescriptionText");
            }
        }

        private string statusBarText = string.Empty;
        public string StatusBarText
        {
            get => statusBarText;
            set
            {
                statusBarText = value;
                OnPropertyUpdate("StatusBarText");
            }
        }

        // True - StatusBar, False - ProgressBar
        private bool switchStatusProgressBar = false;
        public bool SwitchStatusProgressBar
        {
            get => switchStatusProgressBar;
            set
            {
                switchStatusProgressBar = value;
                if (value)
                {
                    BottomStatusBarVisibility = Visibility.Visible;
                    BottomProgressBarVisibility = Visibility.Collapsed;
                }
                else
                {
                    BottomStatusBarVisibility = Visibility.Collapsed;
                    BottomProgressBarVisibility = Visibility.Visible;
                }
            }
        }

        private Visibility bottomStatusBarVisibility = Visibility.Collapsed;
        public Visibility BottomStatusBarVisibility
        {
            get => bottomStatusBarVisibility;
            set
            {
                bottomStatusBarVisibility = value;
                OnPropertyUpdate("BottomStatusBarVisibility");
            }
        }

        private double bottomProgressBarMinimum = 0;
        public double BottomProgressBarMinimum
        {
            get => bottomProgressBarMinimum;
            set
            {
                bottomProgressBarMinimum = value;
                OnPropertyUpdate("BottomProgressBarMinimum");
            }
        }

        private double bottomProgressBarMaximum = 100;
        public double BottomProgressBarMaximum
        {
            get => bottomProgressBarMaximum;
            set
            {
                bottomProgressBarMaximum = value;
                OnPropertyUpdate("BottomProgressBarMaximum");
            }
        }

        private double bottomProgressBarValue = 0;
        public double BottomProgressBarValue
        {
            get => bottomProgressBarValue;
            set
            {
                bottomProgressBarValue = value;
                OnPropertyUpdate("BottomProgressBarValue");
            }
        }

        private Visibility bottomProgressBarVisibility = Visibility.Visible;
        public Visibility BottomProgressBarVisibility
        {
            get => bottomProgressBarVisibility;
            set
            {
                bottomProgressBarVisibility = value;
                OnPropertyUpdate("BottomProgressBarVisibility");
            }
        }

        // True - Normal, False - Build
        private bool switchNormalBuildInterface = true;
        public bool SwitchNormalBuildInterface
        {
            get => switchNormalBuildInterface;
            set
            {
                switchNormalBuildInterface = value;
                if (value)
                {
                    NormalInterfaceVisibility = Visibility.Visible;
                    BuildInterfaceVisibility = Visibility.Collapsed;
                }
                else
                {
                    NormalInterfaceVisibility = Visibility.Collapsed;
                    BuildInterfaceVisibility = Visibility.Visible;
                }
            }
        }

        private Visibility normalInterfaceVisibility = Visibility.Visible;
        public Visibility NormalInterfaceVisibility
        {
            get => normalInterfaceVisibility;
            set
            {
                normalInterfaceVisibility = value;
                OnPropertyUpdate("NormalInterfaceVisibility");
            }
        }

        private Visibility buildInterfaceVisibility = Visibility.Collapsed;
        public Visibility BuildInterfaceVisibility
        {
            get => buildInterfaceVisibility;
            set
            {
                buildInterfaceVisibility = value;
                OnPropertyUpdate("BuildInterfaceVisibility");
            }
        }

        private TreeViewModel tree;
        public TreeViewModel Tree
        {
            get => tree;
            set
            {
                tree = value;
                OnPropertyUpdate("Tree");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyUpdate(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    #endregion

    #region TreeViewModel
    public class TreeViewModel : INotifyPropertyChanged
    {
        private TreeViewModel root;
        public TreeViewModel Root { get => root; }

        private TreeViewModel parent;
        public TreeViewModel Parent { get => parent; }

        public TreeViewModel(TreeViewModel root, TreeViewModel parent)
        {
            this.root = root;
            this.parent = parent;
        }

        private bool isExpanded = false;
        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                isExpanded = value;
                OnPropertyUpdate("IsExpanded");
            }
        }


        public bool Checked
        {
            get
            {
                switch (node.Data.Selected)
                {
                    case SelectedState.True:
                        return true;
                    default:
                        return false;
                }
            }
            set
            {
                MainWindow w = (Application.Current.MainWindow as MainWindow);
                w.Dispatcher.Invoke(() =>
                {
                    w.Model.ProgressRingActive = true;
                    if (node.Data.Mandatory == false && node.Data.Selected != SelectedState.None)
                    {
                        if (value)
                        {
                            node.Data.Selected = SelectedState.True;

                            try
                            {
                                // Run 'Disable' directive
                                DisablePlugins(root, node);
                            }
                            catch (Exception e)
                            {
                                w.Logger.System_Write(new LogInfo(LogState.Error, e));
                            }
                        }
                        else
                        {
                            node.Data.Selected = SelectedState.False;
                        }


                        if (node.Data.Level != Project.MainLevel)
                        {
                            if (0 < this.Child.Count)
                            { // Set child plugins, too -> Top-down propagation
                                foreach (TreeViewModel childModel in this.Child)
                                {
                                    if (value)
                                        childModel.Checked = true;
                                    else
                                        childModel.Checked = false;
                                }
                            }

                            ParentCheckedPropagation();
                        }

                        OnPropertyUpdate("Checked");
                    }
                    w.Model.ProgressRingActive = false;
                });
            }
        }

        public void ParentCheckedPropagation()
        { // Bottom-up propagation of Checked property
            if (parent == null)
                return;

            bool setParentChecked = false;

            foreach (TreeViewModel sibling in parent.Child)
            { // Siblings
                if (sibling.Checked)
                    setParentChecked = true;
            }

            parent.SetParentChecked(setParentChecked);
        }

        public void SetParentChecked(bool value)
        {
            if (parent == null)
                return;

            if (node.Data.Mandatory == false && node.Data.Selected != SelectedState.None)
            {
                if (value)
                    node.Data.Selected = SelectedState.True;
                else
                    node.Data.Selected = SelectedState.False;
            }

            OnPropertyUpdate("Checked");
            ParentCheckedPropagation();
        }

        public Visibility CheckBoxVisible
        {
            get
            {
                if (node.Data.Selected == SelectedState.None)
                    return Visibility.Collapsed;
                else
                    return Visibility.Visible;
            }
        }

        public string Text { get => node.Data.Title; }

        private Node<Plugin> node;
        public Node<Plugin> Node
        {
            get => node;
            set
            {
                node = value;
                OnPropertyUpdate("Node");
            }
        }

        private Control icon;
        public Control Icon
        {
            get => icon;
            set
            {
                icon = value;
                OnPropertyUpdate("Icon");
            }
        }

        private ObservableCollection<TreeViewModel> child = new ObservableCollection<TreeViewModel>();
        public ObservableCollection<TreeViewModel> Child { get => child; }

        public void SetIcon(Control icon)
        {
            this.icon = icon;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyUpdate(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private TreeViewModel FindPluginByFullPath(string fullPath)
        {
            return RecursiveFindPluginByFullPath(root, fullPath); 
        }

        private static TreeViewModel RecursiveFindPluginByFullPath(TreeViewModel cur, string fullPath)
        {
            if (cur.Node != null && cur.Node.Data != null)
            {
                if (fullPath.Equals(cur.Node.Data.FullPath, StringComparison.OrdinalIgnoreCase))
                    return cur;
            }

            if (0 < cur.Child.Count)
            {
                foreach (TreeViewModel next in cur.Child)
                {
                    TreeViewModel found = RecursiveFindPluginByFullPath(next, fullPath);
                    if (found != null)
                        return found;
                }
            }

            // Not found in this path
            return null;
        }

        private void DisablePlugins(TreeViewModel root, Node<Plugin> node)
        {
            if (root == null || node == null || node.Data == null)
                return;

            Plugin p = node.Data;
            List<string> paths = Plugin.GetDisablePluginPaths(p);

            if (paths != null)
            {
                foreach (string path in paths)
                {
                    string fullPath = p.Project.Variables.Expand(path);

                    Plugin pToDisable = p.Project.AllPluginList.FirstOrDefault(x => x.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
                    if (pToDisable != null)
                    {
                        Ini.SetKey(fullPath, "Main", "Selected", "False");
                        TreeViewModel found = FindPluginByFullPath(fullPath);
                        if (found != null)
                        {
                            if (node.Data.Type != PluginType.Directory && node.Data.Mandatory == false && node.Data.Selected != SelectedState.None)
                            {
                                found.Checked = false;
                            }
                        }
                    }
                }
            }            
        }
    }
    #endregion
}