﻿using ComputerVision;
using HtmlAgilityPack;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32.SafeHandles;
using RX_Explorer.Class;
using RX_Explorer.Dialog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Devices.Input;
using Windows.Devices.Radios;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using TreeViewNode = Microsoft.UI.Xaml.Controls.TreeViewNode;

namespace RX_Explorer
{
    public sealed partial class FilePresenter : Page
    {
        public ObservableCollection<FileSystemStorageItemBase> FileCollection { get; private set; }

        private FileControl Container
        {
            get
            {
                if (WeakToFileControl.TryGetTarget(out FileControl Instance))
                {
                    return Instance;
                }
                else
                {
                    return null;
                }
            }
        }

        public WeakReference<FileControl> WeakToFileControl { get; set; }

        private int DropLock;

        private int ViewDropLock;

        private CancellationTokenSource HashCancellation;

        private ListViewBase itemPresenter;
        public ListViewBase ItemPresenter
        {
            get => itemPresenter;
            set
            {
                if (value != itemPresenter)
                {
                    itemPresenter = value;

                    if (value is GridView)
                    {
                        if (ListViewRefreshContainer != null)
                        {
                            ListViewRefreshContainer.Visibility = Visibility.Collapsed;
                            ListViewControl.ItemsSource = null;
                        }

                        GridViewControl.ItemsSource = FileCollection;
                        GridViewRefreshContainer.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        if (GridViewRefreshContainer != null)
                        {
                            GridViewRefreshContainer.Visibility = Visibility.Collapsed;
                            GridViewControl.ItemsSource = null;
                        }

                        ListViewControl.ItemsSource = FileCollection;
                        ListViewRefreshContainer.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        private WiFiShareProvider WiFiProvider;
        private FileSystemStorageItemBase TabTarget;
        private FileSystemStorageItemBase CurrentNameEditItem;
        private DateTimeOffset LastClickTime;
        private DateTimeOffset LastPressTime;
        private string LastPressString;

        public FileSystemStorageItemBase SelectedItem
        {
            get => ItemPresenter.SelectedItem as FileSystemStorageItemBase;
            set
            {
                ItemPresenter.SelectedItem = value;

                if (value != null)
                {
                    (ItemPresenter.ContainerFromItem(value) as ListViewItem)?.Focus(FocusState.Programmatic);
                }
            }
        }

        public List<FileSystemStorageItemBase> SelectedItems => ItemPresenter.SelectedItems.Select((Item) => Item as FileSystemStorageItemBase).ToList();

        public FilePresenter()
        {
            InitializeComponent();

            FileCollection = new ObservableCollection<FileSystemStorageItemBase>();
            FileCollection.CollectionChanged += FileCollection_CollectionChanged;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ZipStrings.CodePage = 936;

            Loaded += FilePresenter_Loaded;
            Unloaded += FilePresenter_Unloaded;

            TryUnlock.IsEnabled = Package.Current.Id.Architecture == ProcessorArchitecture.X64 || Package.Current.Id.Architecture == ProcessorArchitecture.X86 || Package.Current.Id.Architecture == ProcessorArchitecture.X86OnArm64;
        }

        private void FilePresenter_Unloaded(object sender, RoutedEventArgs e)
        {
            CoreWindow.GetForCurrentThread().KeyDown -= Window_KeyDown;
            Dispatcher.AcceleratorKeyActivated -= Dispatcher_AcceleratorKeyActivated;
        }

        private void FilePresenter_Loaded(object sender, RoutedEventArgs e)
        {
            CoreWindow.GetForCurrentThread().KeyDown += Window_KeyDown;
            Dispatcher.AcceleratorKeyActivated += Dispatcher_AcceleratorKeyActivated;
        }

        private void Dispatcher_AcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args.KeyStatus.IsMenuKeyDown)
            {
                switch (args.VirtualKey)
                {
                    case VirtualKey.Left:
                        {
                            Container.GoBackRecord_Click(null, null);
                            break;
                        }
                    case VirtualKey.Right:
                        {
                            Container.GoForwardRecord_Click(null, null);
                            break;
                        }
                }
            }
        }

        private async void Window_KeyDown(CoreWindow sender, KeyEventArgs args)
        {
            CoreVirtualKeyStates CtrlState = sender.GetKeyState(VirtualKey.Control);
            CoreVirtualKeyStates ShiftState = sender.GetKeyState(VirtualKey.Shift);

            bool HasHiddenItem = SelectedItems.Any((Item) => Item is HiddenStorageItem);
            bool AnyCommandBarFlyoutOpened = LnkItemFlyout.IsOpen || EmptyFlyout.IsOpen || FolderFlyout.IsOpen || FileFlyout.IsOpen || HiddenItemFlyout.IsOpen || MixedFlyout.IsOpen;

            if (!QueueContentDialog.IsRunningOrWaiting && !MainPage.ThisPage.IsAnyTaskRunning && !AnyCommandBarFlyoutOpened)
            {
                args.Handled = true;

                if (!CtrlState.HasFlag(CoreVirtualKeyStates.Down) && !ShiftState.HasFlag(CoreVirtualKeyStates.Down))
                {
                    NavigateToStorageItem(args.VirtualKey);
                    return;
                }

                switch (args.VirtualKey)
                {
                    case VirtualKey.Space when SelectedItem != null && SettingControl.IsQuicklookAvailable && SettingControl.IsQuicklookEnable:
                        {
                            await FullTrustProcessController.Current.ViewWithQuicklookAsync(SelectedItem.Path).ConfigureAwait(false);
                            break;
                        }
                    case VirtualKey.Delete:
                        {
                            Delete_Click(null, null);
                            break;
                        }
                    case VirtualKey.F2 when !HasHiddenItem:
                        {
                            Rename_Click(null, null);
                            break;
                        }
                    case VirtualKey.F5:
                        {
                            Refresh_Click(null, null);
                            break;
                        }
                    case VirtualKey.Enter when SelectedItems.Count == 1 && SelectedItem is FileSystemStorageItemBase Item && !HasHiddenItem:
                        {
                            await EnterSelectedItem(Item).ConfigureAwait(false);
                            break;
                        }
                    case VirtualKey.Back:
                        {
                            Container.GoBackRecord_Click(null, null);
                            break;
                        }
                    case VirtualKey.L when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            Container.AddressBox.Focus(FocusState.Programmatic);
                            break;
                        }
                    case VirtualKey.V when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            Paste_Click(null, null);
                            break;
                        }
                    case VirtualKey.A when CtrlState.HasFlag(CoreVirtualKeyStates.Down) && SelectedItem == null:
                        {
                            ItemPresenter.SelectAll();
                            break;
                        }
                    case VirtualKey.C when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            Copy_Click(null, null);
                            break;
                        }
                    case VirtualKey.X when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            Cut_Click(null, null);
                            break;
                        }
                    case VirtualKey.D when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            Delete_Click(null, null);
                            break;
                        }
                    case VirtualKey.F when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            Container.GlobeSearch.Focus(FocusState.Programmatic);
                            break;
                        }
                    case VirtualKey.N when ShiftState.HasFlag(CoreVirtualKeyStates.Down) && CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            CreateFolder_Click(null, null);
                            break;
                        }
                    case VirtualKey.Z when CtrlState.HasFlag(CoreVirtualKeyStates.Down) && OperationRecorder.Current.Value.Count > 0:
                        {
                            await Ctrl_Z_Click().ConfigureAwait(false);
                            break;
                        }
                    case VirtualKey.E when ShiftState.HasFlag(CoreVirtualKeyStates.Down) && Container.CurrentFolder != null:
                        {
                            _ = await Launcher.LaunchFolderAsync(Container.CurrentFolder);
                            break;
                        }
                    case VirtualKey.T when ShiftState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            OpenInTerminal_Click(null, null);
                            break;
                        }
                    case VirtualKey.T when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            OpenFolderInNewTab_Click(null, null);
                            break;
                        }
                    case VirtualKey.W when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            OpenFolderInNewWindow_Click(null, null);
                            break;
                        }
                    case VirtualKey.G when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                        {
                            ItemOpen_Click(null, null);
                            break;
                        }
                    case VirtualKey.Up:
                    case VirtualKey.Down:
                        {
                            if (SelectedItem is FileSystemStorageItemBase Context)
                            {
                                if (SelectedItems.Count > 1 && SelectedItems.Contains(Context))
                                {
                                    if (SelectedItems.Any((Item) => Item is HiddenStorageItem))
                                    {
                                        MixZip.IsEnabled = false;
                                    }
                                    else
                                    {
                                        MixZip.IsEnabled = true;
                                    }

                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(MixedFlyout).ConfigureAwait(true);
                                }
                                else
                                {
                                    if (Context is HiddenStorageItem)
                                    {
                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(HiddenItemFlyout).ConfigureAwait(true);
                                    }
                                    else if (Context is HyperlinkStorageItem)
                                    {
                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout).ConfigureAwait(true);
                                    }
                                    else
                                    {
                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(Context.StorageType == StorageItemTypes.Folder ? FolderFlyout : FileFlyout).ConfigureAwait(true);
                                    }
                                }
                            }
                            else
                            {
                                SelectedItem = FileCollection.FirstOrDefault();
                            }

                            break;
                        }
                    default:
                        {
                            args.Handled = false;
                            break;
                        }
                }
            }
        }

        private void NavigateToStorageItem(VirtualKey Key)
        {
            char Input = Convert.ToChar(Key);
            
            if (char.IsLetterOrDigit(Input))
            {
                string SearchString = Input.ToString();

                try
                {
                    if (LastPressString != SearchString && (DateTimeOffset.Now - LastPressTime).TotalMilliseconds < 1200)
                    {
                        SearchString = LastPressString + SearchString;

                        IEnumerable<FileSystemStorageItemBase> Group = FileCollection.Where((Item) => Item.Name.StartsWith(SearchString, StringComparison.OrdinalIgnoreCase));

                        if (Group.Any() && (SelectedItem == null || !Group.Contains(SelectedItem)))
                        {
                            SelectedItem = Group.FirstOrDefault();
                            ItemPresenter.ScrollIntoView(SelectedItem);
                        }
                    }
                    else
                    {
                        IEnumerable<FileSystemStorageItemBase> Group = FileCollection.Where((Item) => Item.Name.StartsWith(SearchString, StringComparison.OrdinalIgnoreCase));

                        if (Group.Any())
                        {
                            if (SelectedItem != null)
                            {
                                FileSystemStorageItemBase[] ItemArray = Group.ToArray();

                                int NextIndex = Array.IndexOf(ItemArray, SelectedItem);

                                if (NextIndex != -1)
                                {
                                    if (NextIndex < ItemArray.Length - 1)
                                    {
                                        SelectedItem = ItemArray[NextIndex + 1];
                                    }
                                    else
                                    {
                                        SelectedItem = ItemArray.FirstOrDefault();
                                    }
                                }
                                else
                                {
                                    SelectedItem = ItemArray.FirstOrDefault();
                                }
                            }
                            else
                            {
                                SelectedItem = Group.FirstOrDefault();
                            }

                            ItemPresenter.ScrollIntoView(SelectedItem);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, $"{nameof(NavigateToStorageItem)} throw an exception");
                }
                finally
                {
                    LastPressString = SearchString;
                    LastPressTime = DateTimeOffset.Now;
                }
            }
        }

        private void FileCollection_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                HasFile.Visibility = FileCollection.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 关闭右键菜单
        /// </summary>
        private void Restore()
        {
            FileFlyout.Hide();
            FolderFlyout.Hide();
            EmptyFlyout.Hide();
            MixedFlyout.Hide();
            HiddenItemFlyout.Hide();
            LnkItemFlyout.Hide();
        }

        private async Task Ctrl_Z_Click()
        {
            if (OperationRecorder.Current.Value.Count > 0)
            {
                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Undoing")).ConfigureAwait(true);

                try
                {
                    foreach (string Action in OperationRecorder.Current.Value.Pop())
                    {
                        string[] SplitGroup = Action.Split("||", StringSplitOptions.RemoveEmptyEntries);

                        switch (SplitGroup[1])
                        {
                            case "Move":
                                {
                                    if (Container.CurrentFolder.Path == Path.GetDirectoryName(SplitGroup[3]))
                                    {
                                        StorageFolder OriginFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(SplitGroup[0]));

                                        switch (SplitGroup[2])
                                        {
                                            case "File":
                                                {
                                                    if (Path.GetExtension(SplitGroup[3]) == ".lnk")
                                                    {
                                                        await FullTrustProcessController.Current.MoveAsync(SplitGroup[3], OriginFolder, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else if ((await Container.CurrentFolder.TryGetItemAsync(Path.GetFileName(SplitGroup[3]))) is StorageFile File)
                                                    {
                                                        await FullTrustProcessController.Current.MoveAsync(File, OriginFolder, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else
                                                    {
                                                        throw new FileNotFoundException();
                                                    }

                                                    break;
                                                }
                                            case "Folder":
                                                {
                                                    if ((await Container.CurrentFolder.TryGetItemAsync(Path.GetFileName(SplitGroup[3]))) is StorageFolder Folder)
                                                    {
                                                        await FullTrustProcessController.Current.MoveAsync(Folder, OriginFolder, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else
                                                    {
                                                        throw new FileNotFoundException();
                                                    }

                                                    break;
                                                }
                                        }
                                    }
                                    else if (Container.CurrentFolder.Path == Path.GetDirectoryName(SplitGroup[0]))
                                    {
                                        switch (SplitGroup[2])
                                        {
                                            case "File":
                                                {
                                                    if (Path.GetExtension(SplitGroup[3]) == ".lnk")
                                                    {
                                                        await FullTrustProcessController.Current.MoveAsync(SplitGroup[3], Container.CurrentFolder, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else
                                                    {
                                                        StorageFolder TargetFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(SplitGroup[3]));

                                                        if ((await TargetFolder.TryGetItemAsync(Path.GetFileName(SplitGroup[3]))) is StorageFile File)
                                                        {
                                                            await FullTrustProcessController.Current.MoveAsync(File, Container.CurrentFolder, (s, arg) =>
                                                            {
                                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                                {
                                                                    Container.ProBar.IsIndeterminate = false;
                                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                                }
                                                            }, true).ConfigureAwait(true);
                                                        }
                                                        else
                                                        {
                                                            throw new FileNotFoundException();
                                                        }
                                                    }

                                                    break;
                                                }
                                            case "Folder":
                                                {
                                                    StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(SplitGroup[3]);

                                                    await FullTrustProcessController.Current.MoveAsync(Folder, Container.CurrentFolder, (s, arg) =>
                                                    {
                                                        if (Container.ProBar.Value < arg.ProgressPercentage)
                                                        {
                                                            Container.ProBar.IsIndeterminate = false;
                                                            Container.ProBar.Value = arg.ProgressPercentage;
                                                        }
                                                    }, true).ConfigureAwait(true);

                                                    break;
                                                }
                                        }
                                    }
                                    else
                                    {
                                        StorageFolder OriginFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(SplitGroup[0]));

                                        switch (SplitGroup[2])
                                        {
                                            case "File":
                                                {
                                                    if (Path.GetExtension(SplitGroup[3]) == ".lnk")
                                                    {
                                                        await FullTrustProcessController.Current.MoveAsync(SplitGroup[3], OriginFolder, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else
                                                    {
                                                        StorageFile File = await StorageFile.GetFileFromPathAsync(SplitGroup[3]);

                                                        await FullTrustProcessController.Current.MoveAsync(File, OriginFolder, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }

                                                    break;
                                                }
                                            case "Folder":
                                                {
                                                    StorageFolder Folder = await StorageFolder.GetFolderFromPathAsync(SplitGroup[3]);

                                                    await FullTrustProcessController.Current.MoveAsync(Folder, OriginFolder, (s, arg) =>
                                                    {
                                                        if (Container.ProBar.Value < arg.ProgressPercentage)
                                                        {
                                                            Container.ProBar.IsIndeterminate = false;
                                                            Container.ProBar.Value = arg.ProgressPercentage;
                                                        }
                                                    }, true).ConfigureAwait(true);

                                                    if (!SettingControl.IsDetachTreeViewAndPresenter)
                                                    {
                                                        await Container.FolderTree.RootNodes[0].UpdateAllSubNodeAsync().ConfigureAwait(true);
                                                    }

                                                    break;
                                                }
                                        }
                                    }

                                    break;
                                }
                            case "Copy":
                                {
                                    if (Container.CurrentFolder.Path == Path.GetDirectoryName(SplitGroup[3]))
                                    {
                                        switch (SplitGroup[2])
                                        {
                                            case "File":
                                                {
                                                    if (Path.GetExtension(SplitGroup[3]) == ".lnk")
                                                    {
                                                        await FullTrustProcessController.Current.DeleteAsync(SplitGroup[3], true, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else if ((await Container.CurrentFolder.TryGetItemAsync(Path.GetFileName(SplitGroup[3]))) is StorageFile File)
                                                    {
                                                        await FullTrustProcessController.Current.DeleteAsync(File, true, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else
                                                    {
                                                        throw new FileNotFoundException();
                                                    }

                                                    break;
                                                }
                                            case "Folder":
                                                {
                                                    if ((await Container.CurrentFolder.TryGetItemAsync(Path.GetFileName(SplitGroup[3]))) is StorageFolder Folder)
                                                    {
                                                        await FullTrustProcessController.Current.DeleteAsync(Folder, true, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }

                                                    break;
                                                }
                                        }
                                    }
                                    else
                                    {
                                        await FullTrustProcessController.Current.DeleteAsync(SplitGroup[3], true, (s, arg) =>
                                        {
                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = arg.ProgressPercentage;
                                            }
                                        }, true).ConfigureAwait(true);

                                        if (!SettingControl.IsDetachTreeViewAndPresenter)
                                        {
                                            await Container.FolderTree.RootNodes[0].UpdateAllSubNodeAsync().ConfigureAwait(true);
                                        }
                                    }
                                    break;
                                }
                            case "Delete":
                                {
                                    if ((await FullTrustProcessController.Current.GetRecycleBinItemsAsync().ConfigureAwait(true)).FirstOrDefault((Item) => Item.OriginPath == SplitGroup[0]) is FileSystemStorageItemBase Item)
                                    {
                                        if (!await FullTrustProcessController.Current.RestoreItemInRecycleBinAsync(Item.Path).ConfigureAwait(true))
                                        {
                                            QueueContentDialog Dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = $"{Globalization.GetString("QueueDialog_RecycleBinRestoreError_Content")} {Environment.NewLine}{Item.Name}",
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };
                                            _ = Dialog.ShowAsync().ConfigureAwait(true);
                                        }
                                    }
                                    else
                                    {
                                        QueueContentDialog Dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_WarningTitle"),
                                            Content = Globalization.GetString("QueueDialog_UndoFailure_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };
                                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                    break;
                                }
                        }
                    }
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_WarningTitle"),
                        Content = Globalization.GetString("QueueDialog_UndoFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }

                await Container.LoadingActivation(false).ConfigureAwait(false);
            }
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItems.Count > 0)
            {
                try
                {
                    Clipboard.Clear();

                    DataPackage Package = new DataPackage
                    {
                        RequestedOperation = DataPackageOperation.Copy
                    };

                    List<IStorageItem> TempItemList = new List<IStorageItem>(SelectedItems.Count);

                    foreach (FileSystemStorageItemBase Item in SelectedItems.Where((Item) => !(Item is HyperlinkStorageItem || Item is HiddenStorageItem)))
                    {
                        if (await Item.GetStorageItem().ConfigureAwait(true) is IStorageItem It)
                        {
                            TempItemList.Add(It);
                        }
                    }

                    if (TempItemList.Count > 0)
                    {
                        Package.SetStorageItems(TempItemList, false);
                    }

                    List<FileSystemStorageItemBase> NotStorageItems = SelectedItems.Where((Item) => Item is HyperlinkStorageItem || Item is HiddenStorageItem).ToList();

                    if (NotStorageItems.Count > 0)
                    {
                        StringBuilder Builder = new StringBuilder("<head>RX-Explorer-TransferNotStorageItem</head>");

                        foreach (FileSystemStorageItemBase Item in NotStorageItems)
                        {
                            Builder.Append($"<p>{Item.Path}</p>");
                        }

                        Package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(Builder.ToString()));
                    }

                    Clipboard.SetContent(Package);

                    FileCollection.Where((Item) => Item.ThumbnailOpacity != 1d).ToList().ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.Normal));
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnableAccessClipboard_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(false);
                }
            }
        }

        private async void Paste_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            try
            {
                DataPackageView Package = Clipboard.GetContent();

                if (Package.Contains(StandardDataFormats.StorageItems))
                {
                    IReadOnlyList<IStorageItem> ItemList = await Package.GetStorageItemsAsync();

                    if (Package.RequestedOperation.HasFlag(DataPackageOperation.Move))
                    {
                        if (ItemList.Select((Item) => Item.Path).All((Item) => Path.GetDirectoryName(Item) == Container.CurrentFolder.Path))
                        {
                            return;
                        }

                        await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Moving")).ConfigureAwait(true);

                    Retry:
                        try
                        {
                            await FullTrustProcessController.Current.MoveAsync(ItemList, Container.CurrentFolder, (s, arg) =>
                            {
                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                {
                                    Container.ProBar.IsIndeterminate = false;
                                    Container.ProBar.Value = arg.ProgressPercentage;
                                }
                            }).ConfigureAwait(true);
                        }
                        catch (FileNotFoundException)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_MoveFailForNotExist_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                        }
                        catch (FileCaputureException)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                        }
                        catch (InvalidOperationException)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                            };

                            if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                {
                                    goto Retry;
                                }
                                else
                                {
                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_MoveFailUnexpectError_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                        }
                    }
                    else if (Package.RequestedOperation.HasFlag(DataPackageOperation.Copy))
                    {
                        await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                    Retry:
                        try
                        {
                            await FullTrustProcessController.Current.CopyAsync(ItemList, Container.CurrentFolder, (s, arg) =>
                            {
                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                {
                                    Container.ProBar.IsIndeterminate = false;
                                    Container.ProBar.Value = arg.ProgressPercentage;
                                }
                            }).ConfigureAwait(true);
                        }
                        catch (FileNotFoundException)
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_CopyFailForNotExist_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await Dialog.ShowAsync().ConfigureAwait(true);
                        }
                        catch (InvalidOperationException)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                            };

                            if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                {
                                    goto Retry;
                                }
                                else
                                {
                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                        }
                    }
                }

                if (Package.Contains(StandardDataFormats.Html))
                {
                    string Html = await Package.GetHtmlFormatAsync();
                    string Fragment = HtmlFormatHelper.GetStaticFragment(Html);

                    HtmlDocument Document = new HtmlDocument();
                    Document.LoadHtml(Fragment);
                    HtmlNode HeadNode = Document.DocumentNode.SelectSingleNode("/head");

                    if (HeadNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                    {
                        HtmlNodeCollection BodyNode = Document.DocumentNode.SelectNodes("/p");
                        List<string> LinkItemsPath = BodyNode.Select((Node) => Node.InnerText).ToList();

                        if (Package.RequestedOperation.HasFlag(DataPackageOperation.Move))
                        {
                            if (LinkItemsPath.All((Item) => Path.GetDirectoryName(Item) == Container.CurrentFolder.Path))
                            {
                                return;
                            }

                            await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Moving")).ConfigureAwait(true);

                        Retry:
                            try
                            {
                                await FullTrustProcessController.Current.MoveAsync(LinkItemsPath, Container.CurrentFolder.Path, (s, arg) =>
                                {
                                    if (Container.ProBar.Value < arg.ProgressPercentage)
                                    {
                                        Container.ProBar.IsIndeterminate = false;
                                        Container.ProBar.Value = arg.ProgressPercentage;
                                    }
                                }).ConfigureAwait(true);
                            }
                            catch (FileNotFoundException)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_MoveFailForNotExist_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                            }
                            catch (FileCaputureException)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await dialog.ShowAsync().ConfigureAwait(true);

                            }
                            catch (InvalidOperationException)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                    PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                };

                                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                {
                                    if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                    {
                                        goto Retry;
                                    }
                                    else
                                    {
                                        QueueContentDialog ErrorDialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_MoveFailUnexpectError_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                            }
                        }
                        else if (Package.RequestedOperation.HasFlag(DataPackageOperation.Copy))
                        {
                            await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                        Retry:
                            try
                            {
                                await FullTrustProcessController.Current.CopyAsync(LinkItemsPath, Container.CurrentFolder.Path, (s, arg) =>
                                {
                                    if (Container.ProBar.Value < arg.ProgressPercentage)
                                    {
                                        Container.ProBar.IsIndeterminate = false;
                                        Container.ProBar.Value = arg.ProgressPercentage;
                                    }
                                }).ConfigureAwait(true);
                            }
                            catch (FileNotFoundException)
                            {
                                QueueContentDialog Dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_CopyFailForNotExist_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await Dialog.ShowAsync().ConfigureAwait(true);
                            }
                            catch (InvalidOperationException)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                    PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                };

                                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                {
                                    if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                    {
                                        goto Retry;
                                    }
                                    else
                                    {
                                        QueueContentDialog ErrorDialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };
                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                            }
                        }
                    }
                }
            }
            catch
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_FailToGetClipboardError_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await dialog.ShowAsync().ConfigureAwait(true);
            }
            finally
            {
                await Container.LoadingActivation(false).ConfigureAwait(true);
                FileCollection.Where((Item) => Item.ThumbnailOpacity != 1d).ToList().ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.Normal));
            }
        }

        private async void Cut_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItems.Count > 0)
            {
                try
                {
                    Clipboard.Clear();

                    DataPackage Package = new DataPackage
                    {
                        RequestedOperation = DataPackageOperation.Move
                    };

                    List<IStorageItem> TempItemList = new List<IStorageItem>(SelectedItems.Count);
                    foreach (FileSystemStorageItemBase Item in SelectedItems.Where((Item) => !(Item is HyperlinkStorageItem || Item is HiddenStorageItem)))
                    {
                        if (await Item.GetStorageItem().ConfigureAwait(true) is IStorageItem It)
                        {
                            TempItemList.Add(It);
                        }
                    }

                    if (TempItemList.Count > 0)
                    {
                        Package.SetStorageItems(TempItemList, false);
                    }

                    List<FileSystemStorageItemBase> NotStorageItems = SelectedItems.Where((Item) => Item is HyperlinkStorageItem || Item is HiddenStorageItem).ToList();
                    if (NotStorageItems.Count > 0)
                    {
                        StringBuilder Builder = new StringBuilder("<head>RX-Explorer-TransferNotStorageItem</head>");

                        foreach (FileSystemStorageItemBase Item in NotStorageItems)
                        {
                            Builder.Append($"<p>{Item.Path}</p>");
                        }

                        Package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(Builder.ToString()));
                    }

                    Clipboard.SetContent(Package);

                    FileCollection.Where((Item) => Item.ThumbnailOpacity != 1d).ToList().ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.Normal));
                    SelectedItems.ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.ReduceOpacity));
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnableAccessClipboard_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(false);
                }
            }
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItems.Count > 0)
            {
                List<string> PathList = SelectedItems.Select((Item) => Item.Path).ToList();

                if (Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
                {
                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Deleting")).ConfigureAwait(true);

                Retry:
                    try
                    {
                        await FullTrustProcessController.Current.DeleteAsync(PathList, true, (s, arg) =>
                        {
                            if (Container.ProBar.Value < arg.ProgressPercentage)
                            {
                                Container.ProBar.IsIndeterminate = false;
                                Container.ProBar.Value = arg.ProgressPercentage;
                            }
                        }).ConfigureAwait(true);
                    }
                    catch (FileNotFoundException)
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_DeleteItemError_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                        };
                        _ = await Dialog.ShowAsync().ConfigureAwait(true);

                        await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(true);
                    }
                    catch (FileCaputureException)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                    }
                    catch (InvalidOperationException)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnauthorizedDelete_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                        };

                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                        {
                            if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                            {
                                goto Retry;
                            }
                            else
                            {
                                QueueContentDialog ErrorDialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_DeleteFailUnexpectError_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                    }

                    await Container.LoadingActivation(false).ConfigureAwait(false);
                }
                else
                {
                    DeleteDialog QueueContenDialog = new DeleteDialog(Globalization.GetString("QueueDialog_DeleteFiles_Content"));

                    if ((await QueueContenDialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                    {
                        await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Deleting")).ConfigureAwait(true);

                    Retry:
                        try
                        {
                            await FullTrustProcessController.Current.DeleteAsync(PathList, QueueContenDialog.IsPermanentDelete, (s, arg) =>
                            {
                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                {
                                    Container.ProBar.IsIndeterminate = false;
                                    Container.ProBar.Value = arg.ProgressPercentage;
                                }
                            }).ConfigureAwait(true);
                        }
                        catch (FileNotFoundException)
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_DeleteItemError_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                            };
                            _ = await Dialog.ShowAsync().ConfigureAwait(true);

                            await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(true);
                        }
                        catch (FileCaputureException)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                        }
                        catch (InvalidOperationException)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnauthorizedDelete_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                            };

                            if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                {
                                    goto Retry;
                                }
                                else
                                {
                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_DeleteFailUnexpectError_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                        }

                        await Container.LoadingActivation(false).ConfigureAwait(false);
                    }
                }
            }
        }

        private async void Rename_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItems.Count > 0)
            {
                if (SelectedItems.Count > 1)
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_RenameNumError_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
                else
                {
                    FileSystemStorageItemBase RenameItem = SelectedItem;

                    RenameDialog dialog = new RenameDialog(RenameItem);

                    if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                    {
                        if (WIN_Native_API.CheckExist(Path.Combine(Path.GetDirectoryName(RenameItem.Path), dialog.DesireName)))
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_RenameExist_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_ContinueButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                            };

                            if (await Dialog.ShowAsync().ConfigureAwait(true) != ContentDialogResult.Primary)
                            {
                                return;
                            }
                        }

                    Retry:
                        try
                        {
                            await FullTrustProcessController.Current.RenameAsync(RenameItem.Path, dialog.DesireName).ConfigureAwait(true);
                        }
                        catch (FileLoadException)
                        {
                            QueueContentDialog LoadExceptionDialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_FileOccupied_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                            };

                            _ = await LoadExceptionDialog.ShowAsync().ConfigureAwait(true);
                        }
                        catch (InvalidOperationException)
                        {
                            QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnauthorizedRenameFile_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                            };

                            if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                {
                                    goto Retry;
                                }
                                else
                                {
                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                }
                            }
                        }
                        catch
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnauthorizedRename_StartExplorer_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                            };

                            if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                _ = await Launcher.LaunchFolderAsync(Container.CurrentFolder);
                            }
                        }
                    }
                }
            }
        }

        private async void BluetoothShare_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            StorageFile ShareFile = (await SelectedItem.GetStorageItem().ConfigureAwait(true)) as StorageFile;

            if (!WIN_Native_API.CheckExist(ShareFile.Path))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                };
                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                return;
            }

            IReadOnlyList<Radio> RadioDevice = await Radio.GetRadiosAsync();

            if (RadioDevice.Any((Device) => Device.Kind == RadioKind.Bluetooth && Device.State == RadioState.On))
            {
                BluetoothUI Bluetooth = new BluetoothUI();
                if ((await Bluetooth.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                {
                    BluetoothFileTransfer FileTransfer = new BluetoothFileTransfer(ShareFile);

                    _ = await FileTransfer.ShowAsync().ConfigureAwait(true);
                }
            }
            else
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                    Content = Globalization.GetString("QueueDialog_OpenBluetooth_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await dialog.ShowAsync().ConfigureAwait(true);
            }
        }

        private void ViewControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            MixZip.IsEnabled = true;

            if (SelectedItems.Any((Item) => Item.StorageType != StorageItemTypes.Folder))
            {
                if (SelectedItems.All((Item) => Item.StorageType == StorageItemTypes.File))
                {
                    if (SelectedItems.All((Item) => Item.Type == ".zip"))
                    {
                        MixZip.Label = Globalization.GetString("Operate_Text_Decompression");
                    }
                    else if (SelectedItems.All((Item) => Item.Type != ".zip"))
                    {
                        MixZip.Label = Globalization.GetString("Operate_Text_Compression");
                    }
                    else
                    {
                        MixZip.IsEnabled = false;
                    }
                }
                else
                {
                    if (SelectedItems.Where((It) => It.StorageType == StorageItemTypes.File).Any((Item) => Item.Type == ".zip"))
                    {
                        MixZip.IsEnabled = false;
                    }
                    else
                    {
                        MixZip.Label = Globalization.GetString("Operate_Text_Compression");
                    }
                }
            }
            else
            {
                MixZip.Label = Globalization.GetString("Operate_Text_Compression");
            }

            if (SelectedItem is FileSystemStorageItemBase Item)
            {
                if (Item.StorageType == StorageItemTypes.File)
                {
                    FileTool.IsEnabled = true;
                    FileEdit.IsEnabled = false;
                    FileShare.IsEnabled = true;
                    Zip.IsEnabled = true;

                    ChooseOtherApp.IsEnabled = true;
                    RunWithSystemAuthority.IsEnabled = false;

                    Zip.Label = Globalization.GetString("Operate_Text_Compression");

                    switch (Item.Type.ToLower())
                    {
                        case ".zip":
                            {
                                Zip.Label = Globalization.GetString("Operate_Text_Decompression");
                                break;
                            }
                        case ".mp4":
                        case ".wmv":
                            {
                                FileEdit.IsEnabled = true;
                                break;
                            }
                        case ".mkv":
                        case ".m4a":
                        case ".mov":
                        case ".mp3":
                        case ".flac":
                        case ".wma":
                        case ".alac":
                        case ".png":
                        case ".bmp":
                        case ".jpg":
                        case ".heic":
                        case ".gif":
                        case ".tiff":
                            {
                                FileEdit.IsEnabled = true;
                                Transcode.IsEnabled = true;
                                break;
                            }
                        case ".exe":
                            {
                                ChooseOtherApp.IsEnabled = false;
                                RunWithSystemAuthority.IsEnabled = true;
                                break;
                            }
                        case ".bat":
                            {
                                RunWithSystemAuthority.IsEnabled = true;
                                break;
                            }
                        case ".msc":
                            {
                                ChooseOtherApp.IsEnabled = false;
                                break;
                            }
                    }
                }
            }

            string[] StatusTipsSplit = StatusTips.Text.Split("  |  ", StringSplitOptions.RemoveEmptyEntries);

            if (SelectedItems.Count > 0)
            {
                string SizeInfo = string.Empty;

                if (SelectedItems.All((Item) => Item.StorageType == StorageItemTypes.File))
                {
                    ulong TotalSize = 0;
                    foreach (ulong Size in SelectedItems.Select((Item) => Item.SizeRaw).ToArray())
                    {
                        TotalSize += Size;
                    }

                    SizeInfo = $"  |  {TotalSize.ToFileSizeDescription()}";
                }

                if (StatusTipsSplit.Length > 0)
                {
                    StatusTips.Text = $"{StatusTipsSplit[0]}  |  {Globalization.GetString("FilePresenterBottomStatusTip_SelectedItem").Replace("{ItemNum}", SelectedItems.Count.ToString())}{SizeInfo}";
                }
                else
                {
                    StatusTips.Text += $"  |  {Globalization.GetString("FilePresenterBottomStatusTip_SelectedItem").Replace("{ItemNum}", SelectedItems.Count.ToString())}{SizeInfo}";
                }
            }
            else
            {
                if (StatusTipsSplit.Length > 0)
                {
                    StatusTips.Text = StatusTipsSplit[0];
                }
            }
        }

        private async void ViewControl_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext == null)
            {
                SelectedItem = null;
            }
            else if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Item && Item.StorageType == StorageItemTypes.Folder)
                {
                    SelectedItem = Item;
                    await TabViewContainer.ThisPage.CreateNewTabAndOpenTargetFolder(Item.Path).ConfigureAwait(false);
                }
            }
        }

        private async void ViewControl_RightTapped(object sender, Windows.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (e.PointerDeviceType == PointerDeviceType.Mouse)
            {
                e.Handled = true;

                if (ItemPresenter is GridView)
                {
                    if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Context)
                    {
                        if (SelectedItems.Count > 1 && SelectedItems.Contains(Context))
                        {
                            if (SelectedItems.Any((Item) => Item is HiddenStorageItem))
                            {
                                MixZip.IsEnabled = false;
                            }
                            else
                            {
                                MixZip.IsEnabled = true;
                            }

                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(MixedFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                        }
                        else
                        {
                            SelectedItem = Context;

                            if (Context is HiddenStorageItem)
                            {
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(HiddenItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                            else if (Context is HyperlinkStorageItem)
                            {
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                            else
                            {
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(Context.StorageType == StorageItemTypes.Folder ? FolderFlyout : FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                        }
                    }
                    else
                    {
                        SelectedItem = null;
                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                    }
                }
                else
                {
                    if (e.OriginalSource is FrameworkElement Element)
                    {
                        if (Element.Name == "EmptyTextblock")
                        {
                            SelectedItem = null;
                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                        }
                        else
                        {
                            if (Element.DataContext is FileSystemStorageItemBase Context)
                            {
                                if (SelectedItems.Count > 1 && SelectedItems.Contains(Context))
                                {
                                    if (SelectedItems.Any((Item) => Item is HiddenStorageItem))
                                    {
                                        MixZip.IsEnabled = false;
                                    }
                                    else
                                    {
                                        MixZip.IsEnabled = true;
                                    }

                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(MixedFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                }
                                else
                                {
                                    if (SelectedItem == Context)
                                    {
                                        if (Context is HiddenStorageItem)
                                        {
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(HiddenItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                        else if (Context is HyperlinkStorageItem)
                                        {
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                        else
                                        {
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(Context.StorageType == StorageItemTypes.Folder ? FolderFlyout : FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                    }
                                    else
                                    {
                                        if (e.OriginalSource is TextBlock)
                                        {
                                            SelectedItem = Context;

                                            if (Context is HiddenStorageItem)
                                            {
                                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(HiddenItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                            }
                                            else if (Context is HyperlinkStorageItem)
                                            {
                                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                            }
                                            else
                                            {
                                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(Context.StorageType == StorageItemTypes.Folder ? FolderFlyout : FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                            }
                                        }
                                        else
                                        {
                                            SelectedItem = null;
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                SelectedItem = null;
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                        }
                    }
                }
            }
        }

        private async void FileProperty_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            PropertyDialog Dialog = new PropertyDialog(SelectedItem);
            _ = await Dialog.ShowAsync().ConfigureAwait(true);
        }

        private async void Zip_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            StorageFile Item = (await SelectedItem.GetStorageItem().ConfigureAwait(true)) as StorageFile;

            if (!WIN_Native_API.CheckExist(Item.Path))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                };
                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                return;
            }

            if (Item.FileType == ".zip")
            {
                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Extracting")).ConfigureAwait(true);

                await UnZipAsync(Item, (s, e) =>
                {
                    if (Container.ProBar.Value < e.ProgressPercentage)
                    {
                        Container.ProBar.IsIndeterminate = false;
                        Container.ProBar.Value = e.ProgressPercentage;
                    }
                }).ConfigureAwait(true);

                await Container.LoadingActivation(false).ConfigureAwait(true);
            }
            else
            {
                ZipDialog dialog = new ZipDialog(Item.DisplayName);

                if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                {
                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Compressing")).ConfigureAwait(true);

                    await CreateZipAsync(Item, dialog.FileName, (int)dialog.Level, (s, e) =>
                    {
                        if (Container.ProBar.Value < e.ProgressPercentage)
                        {
                            Container.ProBar.IsIndeterminate = false;
                            Container.ProBar.Value = e.ProgressPercentage;
                        }
                    }).ConfigureAwait(true);

                    await Container.LoadingActivation(false).ConfigureAwait(false);
                }
            }
        }

        private async Task UnZipAsync(IEnumerable<FileSystemStorageItemBase> FileList, ProgressChangedEventHandler ProgressHandler = null)
        {
            long TotalSize = 0;

            foreach (FileSystemStorageItemBase Item in FileList)
            {
                TotalSize += Convert.ToInt64(Item.SizeRaw);
            }

            if (TotalSize == 0)
            {
                return;
            }

            await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Extracting")).ConfigureAwait(true);

            long Step = 0;

            foreach (FileSystemStorageItemBase Item in FileList)
            {
                if (await Item.GetStorageItem().ConfigureAwait(true) is StorageFile File)
                {
                    await UnZipAsync(File, (s, e) =>
                    {
                        ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(Convert.ToInt32(Math.Ceiling((Convert.ToDouble(e.ProgressPercentage * Convert.ToInt64(Item.SizeRaw)) + Step * 100) / TotalSize)), null));
                    }).ConfigureAwait(true);

                    Step += Convert.ToInt64(Item.SizeRaw);
                }
            }

            await Container.LoadingActivation(false).ConfigureAwait(true);
        }

        /// <summary>
        /// 执行ZIP解压功能
        /// </summary>
        /// <param name="ZFile">ZIP文件</param>
        /// <returns>无</returns>
        private async Task UnZipAsync(StorageFile ZFile, ProgressChangedEventHandler ProgressHandler = null)
        {
            StorageFolder ParentFolder = null;
            StorageFolder NewFolder = null;

            try
            {
                ParentFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(ZFile.Path));
                NewFolder = await ParentFolder.CreateFolderAsync(Path.GetFileNameWithoutExtension(ZFile.Name), CreationCollisionOption.OpenIfExists);

                using (SafeFileHandle NewFileLockHandle = ZFile.LockAndBlockAccess())
                using (FileStream FileStream = new FileStream(NewFileLockHandle, FileAccess.ReadWrite))
                using (ZipInputStream InputZipStream = new ZipInputStream(FileStream))
                {
                    FileStream.Seek(0, SeekOrigin.Begin);

                    InputZipStream.IsStreamOwner = false;

                    while (InputZipStream.GetNextEntry() is ZipEntry Entry)
                    {
                        if (!InputZipStream.CanDecompressEntry)
                        {
                            throw new NotImplementedException();
                        }

                        StorageFile NewFile = null;

                        if (Entry.Name.Contains("/"))
                        {
                            string[] SplitFolderPath = Entry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);

                            StorageFolder TempFolder = NewFolder;

                            for (int i = 0; i < SplitFolderPath.Length - 1; i++)
                            {
                                TempFolder = await TempFolder.CreateFolderAsync(SplitFolderPath[i], CreationCollisionOption.OpenIfExists);
                            }

                            if (Entry.Name.Last() == '/')
                            {
                                await TempFolder.CreateFolderAsync(SplitFolderPath.Last(), CreationCollisionOption.OpenIfExists);
                                continue;
                            }
                            else
                            {
                                NewFile = await TempFolder.CreateFileAsync(SplitFolderPath.Last(), CreationCollisionOption.ReplaceExisting);
                            }
                        }
                        else
                        {
                            NewFile = await NewFolder.CreateFileAsync(Entry.Name, CreationCollisionOption.ReplaceExisting);
                        }

                        using (Stream NewFileStream = await NewFile.OpenStreamForWriteAsync().ConfigureAwait(true))
                        {
                            await InputZipStream.CopyToAsync(NewFileStream).ConfigureAwait(true);
                        }

                        ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(Convert.ToInt32(Math.Ceiling(FileStream.Position * 100d / FileStream.Length)), null));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (NewFolder != null)
                {
                    await NewFolder.DeleteAllSubFilesAndFolders().ConfigureAwait(true);
                }

                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_UnauthorizedDecompression_Content"),
                    PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                };

                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                {
                    _ = await Launcher.LaunchFolderAsync(ParentFolder);
                }
            }
            catch (NotImplementedException)
            {
                if (NewFolder != null)
                {
                    await NewFolder.DeleteAllSubFilesAndFolders().ConfigureAwait(true);
                }

                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_CanNotDecompressEncrypted_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);
            }
            catch (Exception e)
            {
                if (NewFolder != null)
                {
                    await NewFolder.DeleteAllSubFilesAndFolders().ConfigureAwait(true);
                }

                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_DecompressionError_Content") + e.Message,
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await dialog.ShowAsync().ConfigureAwait(true);
            }
        }

        /// <summary>
        /// 执行ZIP文件创建功能
        /// </summary>
        /// <param name="ZipTarget">待压缩文件</param>
        /// <param name="NewZipName">生成的Zip文件名</param>
        /// <param name="ZipLevel">压缩等级</param>
        /// <param name="ProgressHandler">进度通知</param>
        private async Task CreateZipAsync(IStorageItem ZipTarget, string NewZipName, int ZipLevel, ProgressChangedEventHandler ProgressHandler = null)
        {
            try
            {
                StorageFile Newfile = await Container.CurrentFolder.CreateFileAsync(NewZipName, CreationCollisionOption.GenerateUniqueName);

                using (SafeFileHandle NewFileLockHandle = Newfile.LockAndBlockAccess())
                using (FileStream NewFileStream = new FileStream(NewFileLockHandle, FileAccess.ReadWrite))
                using (ZipOutputStream OutputStream = new ZipOutputStream(NewFileStream))
                {
                    OutputStream.SetLevel(ZipLevel);
                    OutputStream.UseZip64 = UseZip64.Dynamic;
                    OutputStream.IsStreamOwner = false;

                    if (ZipTarget is StorageFile ZipFile)
                    {
                        using (Stream FileStream = await ZipFile.OpenStreamForReadAsync().ConfigureAwait(true))
                        {
                            ZipEntry NewEntry = new ZipEntry(ZipFile.Name)
                            {
                                DateTime = DateTime.Now,
                                CompressionMethod = CompressionMethod.Deflated,
                                Size = FileStream.Length
                            };

                            OutputStream.PutNextEntry(NewEntry);

                            await FileStream.CopyToAsync(OutputStream).ConfigureAwait(true);
                        }

                        ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(100, null));
                    }
                    else if (ZipTarget is StorageFolder ZipFolder)
                    {
                        await ZipFolderCore(ZipFolder, OutputStream, ZipFolder.Name, ProgressHandler).ConfigureAwait(true);
                    }

                    await OutputStream.FlushAsync().ConfigureAwait(true);
                }
            }
            catch (UnauthorizedAccessException)
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_UnauthorizedCompression_Content"),
                    PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                };

                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                {
                    _ = await Launcher.LaunchFolderAsync(Container.CurrentFolder);
                }
            }
            catch (Exception e)
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_CompressionError_Content") + e.Message,
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await dialog.ShowAsync().ConfigureAwait(true);
            }
        }

        /// <summary>
        /// 执行ZIP文件创建功能
        /// </summary>
        /// <param name="FileList">待压缩文件</param>
        /// <param name="NewZipName">生成的Zip文件名</param>
        /// <param name="ZipLevel">压缩等级</param>
        /// <param name="EnableCryption">是否启用加密</param>
        /// <param name="Size">AES加密密钥长度</param>
        /// <param name="Password">密码</param>
        /// <returns>无</returns>
        private async Task CreateZipAsync(IEnumerable<FileSystemStorageItemBase> ZipItemGroup, string NewZipName, int ZipLevel, ProgressChangedEventHandler ProgressHandler = null)
        {
            try
            {
                StorageFile Newfile = await Container.CurrentFolder.CreateFileAsync(NewZipName, CreationCollisionOption.GenerateUniqueName);

                using (SafeFileHandle NewFileLockHandle = Newfile.LockAndBlockAccess())
                using (FileStream NewFileStream = new FileStream(NewFileLockHandle, FileAccess.ReadWrite))
                using (ZipOutputStream OutputStream = new ZipOutputStream(NewFileStream))
                {
                    OutputStream.SetLevel(ZipLevel);
                    OutputStream.UseZip64 = UseZip64.Dynamic;
                    OutputStream.IsStreamOwner = false;

                    try
                    {
                        long TotalSize = 0;

                        foreach (FileSystemStorageItemBase StorageItem in ZipItemGroup)
                        {
                            if (StorageItem.StorageType == StorageItemTypes.File)
                            {
                                TotalSize += Convert.ToInt64(StorageItem.SizeRaw);
                            }
                            else
                            {
                                TotalSize += Convert.ToInt64(WIN_Native_API.CalculateSize(StorageItem.Path));
                            }
                        }

                        long CurrentPosition = 0;

                        foreach (FileSystemStorageItemBase StorageItem in ZipItemGroup)
                        {
                            if (await StorageItem.GetStorageItem().ConfigureAwait(true) is StorageFile ZipFile)
                            {
                                using (Stream FileStream = await ZipFile.OpenStreamForReadAsync().ConfigureAwait(true))
                                {
                                    ZipEntry NewEntry = new ZipEntry(ZipFile.Name)
                                    {
                                        DateTime = DateTime.Now,
                                        CompressionMethod = CompressionMethod.Deflated,
                                        Size = FileStream.Length
                                    };

                                    OutputStream.PutNextEntry(NewEntry);

                                    await FileStream.CopyToAsync(OutputStream).ConfigureAwait(true);
                                }

                                if (TotalSize > 0)
                                {
                                    CurrentPosition += Convert.ToInt64(StorageItem.SizeRaw);
                                    ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(Convert.ToInt32(Math.Ceiling(CurrentPosition * 100d / TotalSize)), null));
                                }
                            }
                            else if (await StorageItem.GetStorageItem().ConfigureAwait(true) is StorageFolder ZipFolder)
                            {
                                long InnerFolderSixe = Convert.ToInt64(WIN_Native_API.CalculateSize(ZipFolder.Path));

                                await ZipFolderCore(ZipFolder, OutputStream, ZipFolder.Name, (s, e) =>
                                {
                                    if (TotalSize > 0)
                                    {
                                        ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(Convert.ToInt32(Math.Ceiling((CurrentPosition + Convert.ToInt64(e.ProgressPercentage / 100d * InnerFolderSixe)) * 100d / TotalSize)), null));
                                    }
                                }).ConfigureAwait(true);

                                if (TotalSize > 0)
                                {
                                    CurrentPosition += InnerFolderSixe;
                                    ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(Convert.ToInt32(Math.Ceiling(CurrentPosition * 100d / TotalSize)), null));
                                }
                            }
                        }

                        await OutputStream.FlushAsync().ConfigureAwait(true);
                    }
                    catch (Exception e)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_CompressionError_Content") + e.Message,
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };
                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_UnauthorizedCompression_Content"),
                    PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                };

                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                {
                    _ = await Launcher.LaunchFolderAsync(Container.CurrentFolder);
                }
            }
        }

        private async Task ZipFolderCore(StorageFolder Folder, ZipOutputStream OutputStream, string BaseFolderName, ProgressChangedEventHandler ProgressHandler = null)
        {
            IReadOnlyList<IStorageItem> ItemsCollection = await Folder.GetItemsAsync();

            if (ItemsCollection.Count == 0)
            {
                if (!string.IsNullOrEmpty(BaseFolderName))
                {
                    ZipEntry NewEntry = new ZipEntry(BaseFolderName);
                    OutputStream.PutNextEntry(NewEntry);
                    OutputStream.CloseEntry();
                }
            }
            else
            {
                long TotalSize = Convert.ToInt64(WIN_Native_API.CalculateSize(Folder.Path));

                long CurrentPosition = 0;

                foreach (IStorageItem Item in ItemsCollection)
                {
                    if (Item is StorageFolder InnerFolder)
                    {
                        long InnerFolderSixe = Convert.ToInt64(WIN_Native_API.CalculateSize(InnerFolder.Path));

                        await ZipFolderCore(InnerFolder, OutputStream, $"{BaseFolderName}/{InnerFolder.Name}", ProgressHandler: (s, e) =>
                        {
                            if (TotalSize > 0)
                            {
                                ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(Convert.ToInt32(Math.Ceiling((CurrentPosition + Convert.ToInt64(e.ProgressPercentage / 100d * InnerFolderSixe)) * 100d / TotalSize)), null));
                            }
                        }).ConfigureAwait(true);

                        if (TotalSize > 0)
                        {
                            CurrentPosition += InnerFolderSixe;
                            ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(Convert.ToInt32(Math.Ceiling(CurrentPosition * 100d / TotalSize)), null));
                        }
                    }
                    else if (Item is StorageFile InnerFile)
                    {
                        using (Stream FileStream = await InnerFile.OpenStreamForReadAsync().ConfigureAwait(true))
                        {
                            ZipEntry NewEntry = new ZipEntry($"{BaseFolderName}/{InnerFile.Name}")
                            {
                                DateTime = DateTime.Now,
                                CompressionMethod = CompressionMethod.Deflated,
                                Size = FileStream.Length
                            };

                            OutputStream.PutNextEntry(NewEntry);

                            await FileStream.CopyToAsync(OutputStream).ConfigureAwait(true);

                            OutputStream.CloseEntry();
                        }

                        if (TotalSize > 0)
                        {
                            CurrentPosition += Convert.ToInt64(await InnerFile.GetSizeRawDataAsync().ConfigureAwait(true));
                            ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(Convert.ToInt32(Math.Ceiling(CurrentPosition * 100d / TotalSize)), null));
                        }
                    }
                }
            }
        }

        private async void ViewControl_DoubleTapped(object sender, Windows.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;

            if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase ReFile)
            {
                await EnterSelectedItem(ReFile).ConfigureAwait(false);
            }
            else if (e.OriginalSource is Grid)
            {
                if (Path.IsPathRooted(Container.CurrentFolder.Path))
                {
                    MainPage.ThisPage.NavView_BackRequested(null, null);
                }
                else
                {
                    Container.GoParentFolder_Click(null, null);
                }
            }
        }

        private async void Transcode_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if ((await SelectedItem.GetStorageItem().ConfigureAwait(true)) is StorageFile Source)
            {
                if (!WIN_Native_API.CheckExist(Source.Path))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                    return;
                }

                if (GeneralTransformer.IsAnyTransformTaskRunning)
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_TipTitle"),
                        Content = Globalization.GetString("QueueDialog_TaskWorking_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    return;
                }

                switch (Source.FileType)
                {
                    case ".mkv":
                    case ".mp4":
                    case ".mp3":
                    case ".flac":
                    case ".wma":
                    case ".wmv":
                    case ".m4a":
                    case ".mov":
                    case ".alac":
                        {
                            TranscodeDialog dialog = new TranscodeDialog(Source);

                            if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                            {
                                try
                                {
                                    StorageFile DestinationFile = await Container.CurrentFolder.CreateFileAsync(Source.DisplayName + "." + dialog.MediaTranscodeEncodingProfile.ToLower(), CreationCollisionOption.GenerateUniqueName);

                                    await GeneralTransformer.TranscodeFromAudioOrVideoAsync(Source, DestinationFile, dialog.MediaTranscodeEncodingProfile, dialog.MediaTranscodeQuality, dialog.SpeedUp).ConfigureAwait(true);
                                }
                                catch (UnauthorizedAccessException)
                                {
                                    QueueContentDialog Dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_UnauthorizedCreateNewFile_Content"),
                                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                                    };

                                    if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                    {
                                        _ = await Launcher.LaunchFolderAsync(Container.CurrentFolder);
                                    }
                                }
                            }

                            break;
                        }
                    case ".png":
                    case ".bmp":
                    case ".jpg":
                    case ".heic":
                    case ".tiff":
                        {
                            TranscodeImageDialog Dialog = null;
                            using (IRandomAccessStream OriginStream = await Source.OpenAsync(FileAccessMode.Read))
                            {
                                BitmapDecoder Decoder = await BitmapDecoder.CreateAsync(OriginStream);
                                Dialog = new TranscodeImageDialog(Decoder.PixelWidth, Decoder.PixelHeight);
                            }

                            if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Transcoding")).ConfigureAwait(true);

                                await GeneralTransformer.TranscodeFromImageAsync(Source, Dialog.TargetFile, Dialog.IsEnableScale, Dialog.ScaleWidth, Dialog.ScaleHeight, Dialog.InterpolationMode).ConfigureAwait(true);

                                await Container.LoadingActivation(false).ConfigureAwait(true);
                            }
                            break;
                        }
                }
            }
        }

        private async void FolderProperty_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            StorageFolder Device = (await SelectedItem.GetStorageItem().ConfigureAwait(true)) as StorageFolder;

            if (!WIN_Native_API.CheckExist(Device.Path))
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                };
                _ = await dialog.ShowAsync().ConfigureAwait(true);

                await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                return;
            }

            PropertyDialog Dialog = new PropertyDialog(SelectedItem);
            _ = await Dialog.ShowAsync().ConfigureAwait(true);
        }

        private async void WIFIShare_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if ((await SelectedItem.GetStorageItem().ConfigureAwait(true)) is StorageFile Item)
            {
                if (QRTeachTip.IsOpen)
                {
                    QRTeachTip.IsOpen = false;
                }

                await Task.Run(() =>
                {
                    SpinWait.SpinUntil(() => WiFiProvider == null);
                }).ConfigureAwait(true);

                WiFiProvider = new WiFiShareProvider();
                WiFiProvider.ThreadExitedUnexpectly += WiFiProvider_ThreadExitedUnexpectly;

                string Hash = Item.Path.ComputeMD5Hash();
                QRText.Text = WiFiProvider.CurrentUri + Hash;
                WiFiProvider.FilePathMap = new KeyValuePair<string, string>(Hash, Item.Path);

                QrCodeEncodingOptions options = new QrCodeEncodingOptions()
                {
                    DisableECI = true,
                    CharacterSet = "UTF-8",
                    Width = 250,
                    Height = 250,
                    ErrorCorrection = ErrorCorrectionLevel.Q
                };

                BarcodeWriter Writer = new BarcodeWriter
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = options
                };

                WriteableBitmap Bitmap = Writer.Write(QRText.Text);
                using (SoftwareBitmap PreTransImage = SoftwareBitmap.CreateCopyFromBuffer(Bitmap.PixelBuffer, BitmapPixelFormat.Bgra8, 250, 250))
                using (SoftwareBitmap TransferImage = ComputerVisionProvider.ExtendImageBorder(PreTransImage, Colors.White, 0, 75, 75, 0))
                {
                    SoftwareBitmapSource Source = new SoftwareBitmapSource();
                    QRImage.Source = Source;
                    await Source.SetBitmapAsync(TransferImage);
                }

                await Task.Delay(500).ConfigureAwait(true);

                QRTeachTip.Target = ItemPresenter.ContainerFromItem(SelectedItem) as FrameworkElement;
                QRTeachTip.IsOpen = true;

                await WiFiProvider.StartToListenRequest().ConfigureAwait(false);
            }
            else
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                };
                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
            }
        }

        private async void WiFiProvider_ThreadExitedUnexpectly(object sender, Exception e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                QRTeachTip.IsOpen = false;

                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_WiFiError_Content") + e.Message,
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await dialog.ShowAsync().ConfigureAwait(true);
            });
        }

        private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
        {
            DataPackage Package = new DataPackage();
            Package.SetText(QRText.Text);
            Clipboard.SetContent(Package);
        }

        private async void UseSystemFileMananger_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            _ = await Launcher.LaunchFolderAsync(Container.CurrentFolder);
        }

        private async void ParentProperty_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (!WIN_Native_API.CheckExist(Container.CurrentFolder.Path))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                };
                _ = await Dialog.ShowAsync().ConfigureAwait(true);
                return;
            }

            if (Container.CurrentFolder.Path == Path.GetPathRoot(Container.CurrentFolder.Path))
            {
                if (CommonAccessCollection.HardDeviceList.FirstOrDefault((Device) => Device.Name == Container.CurrentFolder.DisplayName) is HardDeviceInfo Info)
                {
                    DeviceInfoDialog dialog = new DeviceInfoDialog(Info);
                    _ = await dialog.ShowAsync().ConfigureAwait(true);
                }
                else
                {
                    PropertyDialog Dialog = new PropertyDialog(Container.CurrentFolder);
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
            }
            else
            {
                PropertyDialog Dialog = new PropertyDialog(Container.CurrentFolder);
                _ = await Dialog.ShowAsync().ConfigureAwait(true);
            }
        }

        private async void ItemOpen_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItem is FileSystemStorageItemBase ReFile)
            {
                await EnterSelectedItem(ReFile).ConfigureAwait(false);
            }
        }

        private void QRText_GotFocus(object sender, RoutedEventArgs e)
        {
            ((TextBox)sender).SelectAll();
        }

        private async void CreateFolder_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (!WIN_Native_API.CheckExist(Container.CurrentFolder.Path))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                };
                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                return;
            }

            try
            {
                StorageFolder NewFolder = await Container.CurrentFolder.CreateFolderAsync(Globalization.GetString("Create_NewFolder_Admin_Name"), CreationCollisionOption.GenerateUniqueName);

                while (true)
                {
                    if (FileCollection.FirstOrDefault((Item) => Item.Path == NewFolder.Path) is FileSystemStorageItemBase NewItem)
                    {
                        ItemPresenter.UpdateLayout();

                        ItemPresenter.ScrollIntoView(NewItem);

                        CurrentNameEditItem = NewItem;

                        if ((ItemPresenter.ContainerFromItem(NewItem) as SelectorItem)?.ContentTemplateRoot is FrameworkElement Element)
                        {
                            if (Element.FindName("NameLabel") is TextBlock NameLabel)
                            {
                                NameLabel.Visibility = Visibility.Collapsed;
                            }

                            if (Element.FindName("NameEditBox") is TextBox EditBox)
                            {
                                EditBox.Text = NewFolder.Name;
                                EditBox.Visibility = Visibility.Visible;
                                EditBox.Focus(FocusState.Programmatic);
                            }

                            MainPage.ThisPage.IsAnyTaskRunning = true;
                        }

                        break;
                    }
                    else
                    {
                        await Task.Delay(500).ConfigureAwait(true);
                    }
                }
            }
            catch
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_UnauthorizedCreateFolder_Content"),
                    PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                };

                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                {
                    _ = await Launcher.LaunchFolderAsync(Container.CurrentFolder);
                }
            }
        }

        private async void EmptyFlyout_Opening(object sender, object e)
        {
            try
            {
                DataPackageView Package = Clipboard.GetContent();

                if (Package.Contains(StandardDataFormats.StorageItems))
                {
                    Paste.IsEnabled = true;
                }
                else if (Package.Contains(StandardDataFormats.Html))
                {
                    string Html = await Package.GetHtmlFormatAsync();
                    string Fragment = HtmlFormatHelper.GetStaticFragment(Html);

                    HtmlDocument Document = new HtmlDocument();
                    Document.LoadHtml(Fragment);
                    HtmlNode HeadNode = Document.DocumentNode.SelectSingleNode("/head");

                    if (HeadNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                    {
                        Paste.IsEnabled = true;
                    }
                    else
                    {
                        Paste.IsEnabled = false;
                    }
                }
                else
                {
                    Paste.IsEnabled = false;
                }
            }
            catch
            {
                Paste.IsEnabled = false;
            }

            if (OperationRecorder.Current.Value.Count > 0)
            {
                Undo.IsEnabled = true;
            }
            else
            {
                Undo.IsEnabled = false;
            }
        }

        private async void SystemShare_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if ((await SelectedItem.GetStorageItem().ConfigureAwait(true)) is StorageFile ShareItem)
            {
                if (!WIN_Native_API.CheckExist(ShareItem.Path))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                    return;
                }

                DataTransferManager.GetForCurrentView().DataRequested += (s, args) =>
                {
                    DataPackage Package = new DataPackage();
                    Package.Properties.Title = ShareItem.DisplayName;
                    Package.Properties.Description = ShareItem.DisplayType;
                    Package.SetStorageItems(new StorageFile[] { ShareItem });
                    args.Request.Data = Package;
                };

                DataTransferManager.ShowShareUI();
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            try
            {
                if (WIN_Native_API.CheckExist(Container.CurrentFolder.Path))
                {
                    await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(true);
                }
                else
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"{ nameof(Refresh_Click)} throw an exception");
            }
        }

        private async void ViewControl_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!SettingControl.IsDoubleClickEnable && ItemPresenter.SelectionMode != ListViewSelectionMode.Multiple && e.ClickedItem is FileSystemStorageItemBase ReFile)
            {
                CoreVirtualKeyStates CtrlState = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control);
                CoreVirtualKeyStates ShiftState = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift);

                if (!CtrlState.HasFlag(CoreVirtualKeyStates.Down) && !ShiftState.HasFlag(CoreVirtualKeyStates.Down))
                {
                    await EnterSelectedItem(ReFile).ConfigureAwait(false);
                }
            }
        }

        public async Task EnterSelectedItem(FileSystemStorageItemBase ReFile, bool RunAsAdministrator = false)
        {
            if (Interlocked.Exchange(ref TabTarget, ReFile) == null)
            {
                try
                {
                    if (WIN_Native_API.CheckIfHidden(ReFile.Path))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_ItemHidden_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await Dialog.ShowAsync().ConfigureAwait(false);

                        return;
                    }

                    if ((await TabTarget.GetStorageItem().ConfigureAwait(true)) is StorageFile File)
                    {
                        if (!WIN_Native_API.CheckExist(File.Path))
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                            };
                            _ = await Dialog.ShowAsync().ConfigureAwait(true);

                            await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                            return;
                        }

                        string AdminExcuteProgram = null;
                        if (ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] is string ProgramExcute)
                        {
                            string SaveUnit = ProgramExcute.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault((Item) => Item.Split('|')[0] == File.FileType);
                            if (!string.IsNullOrEmpty(SaveUnit))
                            {
                                AdminExcuteProgram = SaveUnit.Split('|')[1];
                            }
                        }

                        if (!string.IsNullOrEmpty(AdminExcuteProgram) && AdminExcuteProgram != Globalization.GetString("RX_BuildIn_Viewer_Name"))
                        {
                            bool IsExcuted = false;
                            foreach (string Path in await SQLite.Current.GetProgramPickerRecordAsync(File.FileType).ConfigureAwait(true))
                            {
                                try
                                {
                                    StorageFile ExcuteFile = await StorageFile.GetFileFromPathAsync(Path);

                                    string AppName = Convert.ToString((await ExcuteFile.Properties.RetrievePropertiesAsync(new string[] { "System.FileDescription" }))["System.FileDescription"]);

                                    if (AppName == AdminExcuteProgram || ExcuteFile.DisplayName == AdminExcuteProgram)
                                    {
                                    Retry:
                                        try
                                        {
                                            await FullTrustProcessController.Current.RunAsync(Path, false, false, File.Path).ConfigureAwait(true);
                                        }
                                        catch (InvalidOperationException)
                                        {
                                            QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_UnauthorizedExecute_Content"),
                                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                            };

                                            if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                            {
                                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                {
                                                    goto Retry;
                                                }
                                                else
                                                {
                                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                }
                                            }
                                        }

                                        IsExcuted = true;

                                        break;
                                    }
                                }
                                catch (Exception)
                                {
                                    await SQLite.Current.DeleteProgramPickerRecordAsync(File.FileType, Path).ConfigureAwait(true);
                                }
                            }

                            if (!IsExcuted)
                            {
                                if ((await Launcher.FindFileHandlersAsync(File.FileType)).FirstOrDefault((Item) => Item.DisplayInfo.DisplayName == AdminExcuteProgram) is AppInfo Info)
                                {
                                    if (!await Launcher.LaunchFileAsync(File, new LauncherOptions { TargetApplicationPackageFamilyName = Info.PackageFamilyName, DisplayApplicationPicker = false }))
                                    {
                                        ProgramPickerDialog Dialog = new ProgramPickerDialog(File);

                                        if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                        {
                                            if (Dialog.SelectedProgram.PackageName == Package.Current.Id.FamilyName)
                                            {
                                                switch (File.FileType.ToLower())
                                                {
                                                    case ".jpg":
                                                    case ".png":
                                                    case ".bmp":
                                                        {
                                                            if (AnimationController.Current.IsEnableAnimation)
                                                            {
                                                                Container.Frame.Navigate(typeof(PhotoViewer), File.Path, new DrillInNavigationTransitionInfo());
                                                            }
                                                            else
                                                            {
                                                                Container.Frame.Navigate(typeof(PhotoViewer), File.Path, new SuppressNavigationTransitionInfo());
                                                            }
                                                            break;
                                                        }
                                                    case ".mkv":
                                                    case ".mp4":
                                                    case ".mp3":
                                                    case ".flac":
                                                    case ".wma":
                                                    case ".wmv":
                                                    case ".m4a":
                                                    case ".mov":
                                                    case ".alac":
                                                        {
                                                            if (AnimationController.Current.IsEnableAnimation)
                                                            {
                                                                Container.Frame.Navigate(typeof(MediaPlayer), File, new DrillInNavigationTransitionInfo());
                                                            }
                                                            else
                                                            {
                                                                Container.Frame.Navigate(typeof(MediaPlayer), File, new SuppressNavigationTransitionInfo());
                                                            }
                                                            break;
                                                        }
                                                    case ".txt":
                                                        {
                                                            if (AnimationController.Current.IsEnableAnimation)
                                                            {
                                                                Container.Frame.Navigate(typeof(TextViewer), File, new DrillInNavigationTransitionInfo());
                                                            }
                                                            else
                                                            {
                                                                Container.Frame.Navigate(typeof(TextViewer), File, new SuppressNavigationTransitionInfo());
                                                            }
                                                            break;
                                                        }
                                                    case ".pdf":
                                                        {
                                                            if (AnimationController.Current.IsEnableAnimation)
                                                            {
                                                                Container.Frame.Navigate(typeof(PdfReader), File, new DrillInNavigationTransitionInfo());
                                                            }
                                                            else
                                                            {
                                                                Container.Frame.Navigate(typeof(PdfReader), File, new SuppressNavigationTransitionInfo());
                                                            }
                                                            break;
                                                        }
                                                }
                                            }
                                            else
                                            {
                                                if (Dialog.SelectedProgram.IsCustomApp)
                                                {
                                                Retry:
                                                    try
                                                    {
                                                        await FullTrustProcessController.Current.RunAsync(Dialog.SelectedProgram.Path, false, false, File.Path).ConfigureAwait(true);
                                                    }
                                                    catch (InvalidOperationException)
                                                    {
                                                        QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                                                        {
                                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                            Content = Globalization.GetString("QueueDialog_UnauthorizedExecute_Content"),
                                                            PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                        };

                                                        if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                        {
                                                            if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                            {
                                                                goto Retry;
                                                            }
                                                            else
                                                            {
                                                                QueueContentDialog ErrorDialog = new QueueContentDialog
                                                                {
                                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                    Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                                };

                                                                _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                            }
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    if (!await Launcher.LaunchFileAsync(File, new LauncherOptions { TargetApplicationPackageFamilyName = Dialog.SelectedProgram.PackageName, DisplayApplicationPicker = false }))
                                                    {
                                                        if (ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] is string ProgramExcute1)
                                                        {
                                                            ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] = ProgramExcute1.Replace($"{File.FileType}|{File.Name};", string.Empty);
                                                        }

                                                        QueueContentDialog dialog = new QueueContentDialog
                                                        {
                                                            Title = Globalization.GetString("Common_Dialog_TipTitle"),
                                                            Content = Globalization.GetString("QueueDialog_OpenFailure_Content"),
                                                            PrimaryButtonText = Globalization.GetString("QueueDialog_OpenFailure_PrimaryButton"),
                                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                        };

                                                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                        {
                                                            if (!await Launcher.LaunchFileAsync(File))
                                                            {
                                                                LauncherOptions options = new LauncherOptions
                                                                {
                                                                    DisplayApplicationPicker = true
                                                                };
                                                                _ = await Launcher.LaunchFileAsync(File, options);
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    ProgramPickerDialog Dialog = new ProgramPickerDialog(File);

                                    if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                    {
                                        if (Dialog.SelectedProgram.PackageName == Package.Current.Id.FamilyName)
                                        {
                                            switch (File.FileType.ToLower())
                                            {
                                                case ".jpg":
                                                case ".png":
                                                case ".bmp":
                                                    {
                                                        if (AnimationController.Current.IsEnableAnimation)
                                                        {
                                                            Container.Frame.Navigate(typeof(PhotoViewer), File.Path, new DrillInNavigationTransitionInfo());
                                                        }
                                                        else
                                                        {
                                                            Container.Frame.Navigate(typeof(PhotoViewer), File.Path, new SuppressNavigationTransitionInfo());
                                                        }
                                                        break;
                                                    }
                                                case ".mkv":
                                                case ".mp4":
                                                case ".mp3":
                                                case ".flac":
                                                case ".wma":
                                                case ".wmv":
                                                case ".m4a":
                                                case ".mov":
                                                case ".alac":
                                                    {
                                                        if (AnimationController.Current.IsEnableAnimation)
                                                        {
                                                            Container.Frame.Navigate(typeof(MediaPlayer), File, new DrillInNavigationTransitionInfo());
                                                        }
                                                        else
                                                        {
                                                            Container.Frame.Navigate(typeof(MediaPlayer), File, new SuppressNavigationTransitionInfo());
                                                        }
                                                        break;
                                                    }
                                                case ".txt":
                                                    {
                                                        if (AnimationController.Current.IsEnableAnimation)
                                                        {
                                                            Container.Frame.Navigate(typeof(TextViewer), File, new DrillInNavigationTransitionInfo());
                                                        }
                                                        else
                                                        {
                                                            Container.Frame.Navigate(typeof(TextViewer), File, new SuppressNavigationTransitionInfo());
                                                        }
                                                        break;
                                                    }
                                                case ".pdf":
                                                    {
                                                        if (AnimationController.Current.IsEnableAnimation)
                                                        {
                                                            Container.Frame.Navigate(typeof(PdfReader), File, new DrillInNavigationTransitionInfo());
                                                        }
                                                        else
                                                        {
                                                            Container.Frame.Navigate(typeof(PdfReader), File, new SuppressNavigationTransitionInfo());
                                                        }
                                                        break;
                                                    }
                                            }
                                        }
                                        else
                                        {
                                            if (Dialog.SelectedProgram.IsCustomApp)
                                            {
                                            Retry:
                                                try
                                                {
                                                    await FullTrustProcessController.Current.RunAsync(Dialog.SelectedProgram.Path, false, false, File.Path).ConfigureAwait(true);
                                                }
                                                catch (InvalidOperationException)
                                                {
                                                    QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_UnauthorizedExecute_Content"),
                                                        PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                    };

                                                    if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                    {
                                                        if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                        {
                                                            goto Retry;
                                                        }
                                                        else
                                                        {
                                                            QueueContentDialog ErrorDialog = new QueueContentDialog
                                                            {
                                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                            };

                                                            _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                        }
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                if (!await Launcher.LaunchFileAsync(File, new LauncherOptions { TargetApplicationPackageFamilyName = Dialog.SelectedProgram.PackageName, DisplayApplicationPicker = false }))
                                                {
                                                    if (ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] is string ProgramExcute1)
                                                    {
                                                        ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] = ProgramExcute1.Replace($"{File.FileType}|{File.Name};", string.Empty);
                                                    }

                                                    QueueContentDialog dialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_TipTitle"),
                                                        Content = Globalization.GetString("QueueDialog_OpenFailure_Content"),
                                                        PrimaryButtonText = Globalization.GetString("QueueDialog_OpenFailure_PrimaryButton"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                    };

                                                    if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                    {
                                                        if (!await Launcher.LaunchFileAsync(File))
                                                        {
                                                            LauncherOptions options = new LauncherOptions
                                                            {
                                                                DisplayApplicationPicker = true
                                                            };
                                                            _ = await Launcher.LaunchFileAsync(File, options);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            switch (File.FileType.ToLower())
                            {
                                case ".jpg":
                                case ".png":
                                case ".bmp":
                                    {
                                        if (AnimationController.Current.IsEnableAnimation)
                                        {
                                            Container.Frame.Navigate(typeof(PhotoViewer), File.Path, new DrillInNavigationTransitionInfo());
                                        }
                                        else
                                        {
                                            Container.Frame.Navigate(typeof(PhotoViewer), File.Path, new SuppressNavigationTransitionInfo());
                                        }
                                        break;
                                    }
                                case ".mkv":
                                case ".mp4":
                                case ".mp3":
                                case ".flac":
                                case ".wma":
                                case ".wmv":
                                case ".m4a":
                                case ".mov":
                                case ".alac":
                                    {
                                        if (AnimationController.Current.IsEnableAnimation)
                                        {
                                            Container.Frame.Navigate(typeof(MediaPlayer), File, new DrillInNavigationTransitionInfo());
                                        }
                                        else
                                        {
                                            Container.Frame.Navigate(typeof(MediaPlayer), File, new SuppressNavigationTransitionInfo());
                                        }
                                        break;
                                    }
                                case ".txt":
                                    {
                                        if (AnimationController.Current.IsEnableAnimation)
                                        {
                                            Container.Frame.Navigate(typeof(TextViewer), File, new DrillInNavigationTransitionInfo());
                                        }
                                        else
                                        {
                                            Container.Frame.Navigate(typeof(TextViewer), File, new SuppressNavigationTransitionInfo());
                                        }
                                        break;
                                    }
                                case ".pdf":
                                    {
                                        if (AnimationController.Current.IsEnableAnimation)
                                        {
                                            Container.Frame.Navigate(typeof(PdfReader), File, new DrillInNavigationTransitionInfo());
                                        }
                                        else
                                        {
                                            Container.Frame.Navigate(typeof(PdfReader), File, new SuppressNavigationTransitionInfo());
                                        }
                                        break;
                                    }
                                case ".exe":
                                case ".bat":
                                    {
                                    Retry:
                                        try
                                        {
                                            if (TabTarget is HyperlinkStorageItem Item)
                                            {
                                                await FullTrustProcessController.Current.RunAsync(Item.TargetPath, Item.NeedRunAs || RunAsAdministrator, false, Item.Arguments).ConfigureAwait(true);
                                            }
                                            else
                                            {
                                                await FullTrustProcessController.Current.RunAsync(File.Path, RunAsAdministrator).ConfigureAwait(true);
                                            }
                                        }
                                        catch (InvalidOperationException)
                                        {
                                            QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_UnauthorizedExecute_Content"),
                                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                            };

                                            if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                            {
                                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                {
                                                    goto Retry;
                                                }
                                                else
                                                {
                                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                }
                                            }
                                        }

                                        break;
                                    }
                                case ".msc":
                                    {
                                    Retry:
                                        try
                                        {
                                            await FullTrustProcessController.Current.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe"), false, true, "-Command", File.Path).ConfigureAwait(true);
                                        }
                                        catch (InvalidOperationException)
                                        {
                                            QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_UnauthorizedExecute_Content"),
                                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                            };

                                            if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                            {
                                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                {
                                                    goto Retry;
                                                }
                                                else
                                                {
                                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                }
                                            }
                                        }

                                        break;
                                    }
                                default:
                                    {
                                        ProgramPickerDialog Dialog = new ProgramPickerDialog(File);
                                        if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                        {
                                            if (Dialog.SelectedProgram.PackageName == Package.Current.Id.FamilyName)
                                            {
                                                switch (File.FileType.ToLower())
                                                {
                                                    case ".jpg":
                                                    case ".png":
                                                    case ".bmp":
                                                        {
                                                            if (AnimationController.Current.IsEnableAnimation)
                                                            {
                                                                Container.Frame.Navigate(typeof(PhotoViewer), File.Path, new DrillInNavigationTransitionInfo());
                                                            }
                                                            else
                                                            {
                                                                Container.Frame.Navigate(typeof(PhotoViewer), File.Path, new SuppressNavigationTransitionInfo());
                                                            }
                                                            break;
                                                        }
                                                    case ".mkv":
                                                    case ".mp4":
                                                    case ".mp3":
                                                    case ".flac":
                                                    case ".wma":
                                                    case ".wmv":
                                                    case ".m4a":
                                                    case ".mov":
                                                    case ".alac":
                                                        {
                                                            if (AnimationController.Current.IsEnableAnimation)
                                                            {
                                                                Container.Frame.Navigate(typeof(MediaPlayer), File, new DrillInNavigationTransitionInfo());
                                                            }
                                                            else
                                                            {
                                                                Container.Frame.Navigate(typeof(MediaPlayer), File, new SuppressNavigationTransitionInfo());
                                                            }
                                                            break;
                                                        }
                                                    case ".txt":
                                                        {
                                                            if (AnimationController.Current.IsEnableAnimation)
                                                            {
                                                                Container.Frame.Navigate(typeof(TextViewer), File, new DrillInNavigationTransitionInfo());
                                                            }
                                                            else
                                                            {
                                                                Container.Frame.Navigate(typeof(TextViewer), File, new SuppressNavigationTransitionInfo());
                                                            }
                                                            break;
                                                        }
                                                    case ".pdf":
                                                        {
                                                            if (AnimationController.Current.IsEnableAnimation)
                                                            {
                                                                Container.Frame.Navigate(typeof(PdfReader), File, new DrillInNavigationTransitionInfo());
                                                            }
                                                            else
                                                            {
                                                                Container.Frame.Navigate(typeof(PdfReader), File, new SuppressNavigationTransitionInfo());
                                                            }
                                                            break;
                                                        }
                                                }
                                            }
                                            else
                                            {
                                                if (Dialog.SelectedProgram.IsCustomApp)
                                                {
                                                Retry:
                                                    try
                                                    {
                                                        await FullTrustProcessController.Current.RunAsync(Dialog.SelectedProgram.Path, false, false, File.Path).ConfigureAwait(true);
                                                    }
                                                    catch (InvalidOperationException)
                                                    {
                                                        QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                                                        {
                                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                            Content = Globalization.GetString("QueueDialog_UnauthorizedExecute_Content"),
                                                            PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                        };

                                                        if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                        {
                                                            if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                            {
                                                                goto Retry;
                                                            }
                                                            else
                                                            {
                                                                QueueContentDialog ErrorDialog = new QueueContentDialog
                                                                {
                                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                    Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                                };

                                                                _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                            }
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    if (!await Launcher.LaunchFileAsync(File, new LauncherOptions { TargetApplicationPackageFamilyName = Dialog.SelectedProgram.PackageName, DisplayApplicationPicker = false }))
                                                    {
                                                        if (ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] is string ProgramExcute1)
                                                        {
                                                            ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] = ProgramExcute1.Replace($"{File.FileType}|{File.Name};", string.Empty);
                                                        }

                                                        QueueContentDialog dialog = new QueueContentDialog
                                                        {
                                                            Title = Globalization.GetString("Common_Dialog_TipTitle"),
                                                            Content = Globalization.GetString("QueueDialog_OpenFailure_Content"),
                                                            PrimaryButtonText = Globalization.GetString("QueueDialog_OpenFailure_PrimaryButton"),
                                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                        };

                                                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                        {
                                                            if (!await Launcher.LaunchFileAsync(File))
                                                            {
                                                                LauncherOptions options = new LauncherOptions
                                                                {
                                                                    DisplayApplicationPicker = true
                                                                };
                                                                _ = await Launcher.LaunchFileAsync(File, options);
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    }
                            }
                        }
                    }
                    else if ((await TabTarget.GetStorageItem().ConfigureAwait(true)) is StorageFolder Folder)
                    {
                        if (!WIN_Native_API.CheckExist(Folder.Path))
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                            };
                            _ = await Dialog.ShowAsync().ConfigureAwait(true);

                            await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                            return;
                        }

                        if (Folder.Path.StartsWith((Container.FolderTree.RootNodes[0].Content as TreeViewNodeContent).Path))
                        {
                            if (SettingControl.IsDetachTreeViewAndPresenter)
                            {
                                await Container.DisplayItemsInFolder(Folder).ConfigureAwait(true);
                            }
                            else
                            {
                                if (Container.CurrentNode == null)
                                {
                                    Container.CurrentNode = Container.FolderTree.RootNodes[0];
                                }

                                TreeViewNode TargetNode = await Container.FolderTree.RootNodes[0].GetChildNodeAsync(new PathAnalysis(Folder.Path, (Container.FolderTree.RootNodes[0].Content as TreeViewNodeContent).Path)).ConfigureAwait(true);

                                if (TargetNode != null)
                                {
                                    await Container.DisplayItemsInFolder(TargetNode).ConfigureAwait(true);
                                }
                            }
                        }
                        else
                        {
                            await Container.OpenTargetFolder(Folder).ConfigureAwait(true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, $"{ nameof(EnterSelectedItem)} throw an exception");
                }
                finally
                {
                    Interlocked.Exchange(ref TabTarget, null);
                }
            }
        }

        private async void VideoEdit_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (GeneralTransformer.IsAnyTransformTaskRunning)
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                    Content = Globalization.GetString("QueueDialog_TaskWorking_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                return;
            }

            if ((await SelectedItem.GetStorageItem().ConfigureAwait(true)) is StorageFile File)
            {
                VideoEditDialog Dialog = new VideoEditDialog(File);
                if ((await Dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                {
                    StorageFile ExportFile = await Container.CurrentFolder.CreateFileAsync($"{File.DisplayName} - {Globalization.GetString("Crop_Image_Name_Tail")}{Dialog.ExportFileType}", CreationCollisionOption.GenerateUniqueName);

                    await GeneralTransformer.GenerateCroppedVideoFromOriginAsync(ExportFile, Dialog.Composition, Dialog.MediaEncoding, Dialog.TrimmingPreference).ConfigureAwait(true);
                }
            }
        }

        private async void VideoMerge_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (GeneralTransformer.IsAnyTransformTaskRunning)
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                    Content = Globalization.GetString("QueueDialog_TaskWorking_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                return;
            }

            if ((await SelectedItem.GetStorageItem().ConfigureAwait(true)) is StorageFile Item)
            {
                VideoMergeDialog Dialog = new VideoMergeDialog(Item);
                if ((await Dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                {
                    StorageFile ExportFile = await Container.CurrentFolder.CreateFileAsync($"{Item.DisplayName} - {Globalization.GetString("Merge_Image_Name_Tail")}{Dialog.ExportFileType}", CreationCollisionOption.GenerateUniqueName);

                    await GeneralTransformer.GenerateMergeVideoFromOriginAsync(ExportFile, Dialog.Composition, Dialog.MediaEncoding).ConfigureAwait(true);
                }
            }
        }

        private async void ChooseOtherApp_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if ((await SelectedItem.GetStorageItem().ConfigureAwait(true)) is StorageFile Item)
            {
                ProgramPickerDialog Dialog = new ProgramPickerDialog(Item);

                if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                {
                    if (Dialog.SelectedProgram.PackageName == Package.Current.Id.FamilyName)
                    {
                        switch (Item.FileType.ToLower())
                        {
                            case ".jpg":
                            case ".png":
                            case ".bmp":
                                {
                                    if (AnimationController.Current.IsEnableAnimation)
                                    {
                                        Container.Frame.Navigate(typeof(PhotoViewer), Item.Path, new DrillInNavigationTransitionInfo());
                                    }
                                    else
                                    {
                                        Container.Frame.Navigate(typeof(PhotoViewer), Item.Path, new SuppressNavigationTransitionInfo());
                                    }
                                    break;
                                }
                            case ".mkv":
                            case ".mp4":
                            case ".mp3":
                            case ".flac":
                            case ".wma":
                            case ".wmv":
                            case ".m4a":
                            case ".mov":
                            case ".alac":
                                {
                                    if (AnimationController.Current.IsEnableAnimation)
                                    {
                                        Container.Frame.Navigate(typeof(MediaPlayer), Item, new DrillInNavigationTransitionInfo());
                                    }
                                    else
                                    {
                                        Container.Frame.Navigate(typeof(MediaPlayer), Item, new SuppressNavigationTransitionInfo());
                                    }
                                    break;
                                }
                            case ".txt":
                                {
                                    if (AnimationController.Current.IsEnableAnimation)
                                    {
                                        Container.Frame.Navigate(typeof(TextViewer), Item, new DrillInNavigationTransitionInfo());
                                    }
                                    else
                                    {
                                        Container.Frame.Navigate(typeof(TextViewer), Item, new SuppressNavigationTransitionInfo());
                                    }
                                    break;
                                }
                            case ".pdf":
                                {
                                    if (AnimationController.Current.IsEnableAnimation)
                                    {
                                        Container.Frame.Navigate(typeof(PdfReader), Item, new DrillInNavigationTransitionInfo());
                                    }
                                    else
                                    {
                                        Container.Frame.Navigate(typeof(PdfReader), Item, new SuppressNavigationTransitionInfo());
                                    }
                                    break;
                                }
                        }
                    }
                    else
                    {
                        if (Dialog.SelectedProgram.IsCustomApp)
                        {
                        Retry:
                            try
                            {
                                await FullTrustProcessController.Current.RunAsync(Dialog.SelectedProgram.Path, false, false, Item.Path).ConfigureAwait(true);
                            }
                            catch (InvalidOperationException)
                            {
                                QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_UnauthorizedExecute_Content"),
                                    PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                };

                                if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                {
                                    if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                    {
                                        goto Retry;
                                    }
                                    else
                                    {
                                        QueueContentDialog ErrorDialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (!await Launcher.LaunchFileAsync(Item, new LauncherOptions { TargetApplicationPackageFamilyName = Dialog.SelectedProgram.PackageName, DisplayApplicationPicker = false }))
                            {
                                if (ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] is string ProgramExcute)
                                {
                                    ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] = ProgramExcute.Replace($"{Item.FileType}|{Item.Name};", string.Empty);
                                }

                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                                    Content = Globalization.GetString("QueueDialog_OpenFailure_Content"),
                                    PrimaryButtonText = Globalization.GetString("QueueDialog_OpenFailure_PrimaryButton"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                };

                                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                {
                                    if (!await Launcher.LaunchFileAsync(Item))
                                    {
                                        LauncherOptions Options = new LauncherOptions
                                        {
                                            DisplayApplicationPicker = true
                                        };

                                        _ = await Launcher.LaunchFileAsync(Item, Options);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private async void RunWithSystemAuthority_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItem != null)
            {
                await EnterSelectedItem(SelectedItem, true).ConfigureAwait(false);
            }
        }

        private void ListHeaderName_Click(object sender, RoutedEventArgs e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                SortCollectionGenerator.Current.ModifySortWay(SortTarget.Name, SortDirection.Descending);

                List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in SortResult)
                {
                    FileCollection.Add(Item);
                }
            }
            else
            {
                SortCollectionGenerator.Current.ModifySortWay(SortTarget.Name, SortDirection.Ascending);

                List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in SortResult)
                {
                    FileCollection.Add(Item);
                }
            }
        }

        private void ListHeaderModifiedTime_Click(object sender, RoutedEventArgs e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                SortCollectionGenerator.Current.ModifySortWay(SortTarget.ModifiedTime, SortDirection.Descending);

                List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in SortResult)
                {
                    FileCollection.Add(Item);
                }
            }
            else
            {
                SortCollectionGenerator.Current.ModifySortWay(SortTarget.ModifiedTime, SortDirection.Ascending);

                List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in SortResult)
                {
                    FileCollection.Add(Item);
                }
            }
        }

        private void ListHeaderType_Click(object sender, RoutedEventArgs e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                SortCollectionGenerator.Current.ModifySortWay(SortTarget.Type, SortDirection.Descending);

                List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in SortResult)
                {
                    FileCollection.Add(Item);
                }
            }
            else
            {
                SortCollectionGenerator.Current.ModifySortWay(SortTarget.Type, SortDirection.Ascending);

                List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in SortResult)
                {
                    FileCollection.Add(Item);
                }
            }
        }

        private void ListHeaderSize_Click(object sender, RoutedEventArgs e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                SortCollectionGenerator.Current.ModifySortWay(SortTarget.Size, SortDirection.Descending);

                List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);
                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in SortResult)
                {
                    FileCollection.Add(Item);
                }

            }
            else
            {
                SortCollectionGenerator.Current.ModifySortWay(SortTarget.Size, SortDirection.Ascending);

                List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);
                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in SortResult)
                {
                    FileCollection.Add(Item);
                }
            }
        }

        private void QRTeachTip_Closing(TeachingTip sender, TeachingTipClosingEventArgs args)
        {
            QRImage.Source = null;
            WiFiProvider.Dispose();
            WiFiProvider = null;
        }

        private async void CreateFile_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            NewFileDialog Dialog = new NewFileDialog();
            if ((await Dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
            {
                try
                {
                    switch (Path.GetExtension(Dialog.NewFileName))
                    {
                        case ".zip":
                            {
                                _ = await SpecialTypeGenerator.Current.CreateZipAsync(Container.CurrentFolder, Dialog.NewFileName).ConfigureAwait(true) ?? throw new UnauthorizedAccessException();
                                break;
                            }
                        case ".rtf":
                            {
                                _ = await SpecialTypeGenerator.Current.CreateRtfAsync(Container.CurrentFolder, Dialog.NewFileName).ConfigureAwait(true) ?? throw new UnauthorizedAccessException();
                                break;
                            }
                        case ".xlsx":
                            {
                                _ = await SpecialTypeGenerator.Current.CreateExcelAsync(Container.CurrentFolder, Dialog.NewFileName).ConfigureAwait(true) ?? throw new UnauthorizedAccessException();
                                break;
                            }
                        case ".lnk":
                            {
                                LinkOptionsDialog dialog = new LinkOptionsDialog();
                                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                {
                                    if (!await FullTrustProcessController.Current.CreateLink(Path.Combine(Container.CurrentFolder.Path, Dialog.NewFileName), dialog.Path, dialog.Description, dialog.Argument).ConfigureAwait(true))
                                    {
                                        throw new UnauthorizedAccessException();
                                    }
                                }

                                break;
                            }
                        default:
                            {
                                _ = await Container.CurrentFolder.CreateFileAsync(Dialog.NewFileName, CreationCollisionOption.GenerateUniqueName) ?? throw new UnauthorizedAccessException();
                                break;
                            }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedCreateNewFile_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        _ = await Launcher.LaunchFolderAsync(Container.CurrentFolder);
                    }
                }
            }
        }

        private async void CompressFolder_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            StorageFolder Item = (await SelectedItem.GetStorageItem().ConfigureAwait(true)) as StorageFolder;

            if (!WIN_Native_API.CheckExist(Item.Path))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                };
                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                return;
            }

            ZipDialog dialog = new ZipDialog(Item.DisplayName);

            if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
            {
                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Compressing")).ConfigureAwait(true);

                await CreateZipAsync(Item, dialog.FileName, (int)dialog.Level, ProgressHandler: (s, e) =>
                {
                    if (Container.ProBar.Value < e.ProgressPercentage)
                    {
                        Container.ProBar.IsIndeterminate = false;
                        Container.ProBar.Value = e.ProgressPercentage;
                    }
                }).ConfigureAwait(true);

                await Container.LoadingActivation(false).ConfigureAwait(true);
            }
        }

        private void ViewControl_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Html))
            {
                if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                {
                    e.AcceptedOperation = DataPackageOperation.Copy;
                    e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_CopyTo")} {Container.CurrentFolder.DisplayName}";
                }
                else
                {
                    e.AcceptedOperation = DataPackageOperation.Move;
                    e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_MoveTo")} {Container.CurrentFolder.DisplayName}";
                }

                e.DragUIOverride.IsContentVisible = true;
                e.DragUIOverride.IsCaptionVisible = true;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        private async void Item_Drop(object sender, DragEventArgs e)
        {
            DragOperationDeferral Deferral = e.GetDeferral();

            if (Interlocked.Exchange(ref DropLock, 1) == 0)
            {
                try
                {
                    if (e.DataView.Contains(StandardDataFormats.Html))
                    {
                        string Html = await e.DataView.GetHtmlFormatAsync();
                        string Fragment = HtmlFormatHelper.GetStaticFragment(Html);

                        HtmlDocument Document = new HtmlDocument();
                        Document.LoadHtml(Fragment);
                        HtmlNode HeadNode = Document.DocumentNode.SelectSingleNode("/head");

                        if (HeadNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                        {
                            HtmlNodeCollection BodyNode = Document.DocumentNode.SelectNodes("/p");
                            List<string> LinkItemsPath = BodyNode.Select((Node) => Node.InnerText).ToList();

                            if ((sender as SelectorItem).Content is FileSystemStorageItemBase Item)
                            {
                                StorageFolder TargetFolder = (await Item.GetStorageItem().ConfigureAwait(true)) as StorageFolder;

                                switch (e.AcceptedOperation)
                                {
                                    case DataPackageOperation.Copy:
                                        {
                                            await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                                        Retry:
                                            try
                                            {
                                                await FullTrustProcessController.Current.CopyAsync(LinkItemsPath, TargetFolder.Path, (s, arg) =>
                                                {
                                                    if (Container.ProBar.Value < arg.ProgressPercentage)
                                                    {
                                                        Container.ProBar.IsIndeterminate = false;
                                                        Container.ProBar.Value = arg.ProgressPercentage;
                                                    }
                                                }).ConfigureAwait(true);
                                            }
                                            catch (FileNotFoundException)
                                            {
                                                QueueContentDialog Dialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_CopyFailForNotExist_Content"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                };

                                                _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                            }
                                            catch (InvalidOperationException)
                                            {
                                                QueueContentDialog dialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                                    PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                };

                                                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                {
                                                    if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                    {
                                                        goto Retry;
                                                    }
                                                    else
                                                    {
                                                        QueueContentDialog ErrorDialog = new QueueContentDialog
                                                        {
                                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                            Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                        };

                                                        _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                    }
                                                }
                                            }
                                            catch (Exception)
                                            {
                                                QueueContentDialog dialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                };

                                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                                            }

                                            break;
                                        }
                                    case DataPackageOperation.Move:
                                        {
                                            await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Moving")).ConfigureAwait(true);

                                        Retry:
                                            try
                                            {
                                                await FullTrustProcessController.Current.MoveAsync(LinkItemsPath, TargetFolder.Path, (s, arg) =>
                                                {
                                                    if (Container.ProBar.Value < arg.ProgressPercentage)
                                                    {
                                                        Container.ProBar.IsIndeterminate = false;
                                                        Container.ProBar.Value = arg.ProgressPercentage;
                                                    }
                                                }).ConfigureAwait(true);
                                            }
                                            catch (FileNotFoundException)
                                            {
                                                QueueContentDialog Dialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_MoveFailForNotExist_Content"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                };

                                                _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                            }
                                            catch (FileCaputureException)
                                            {
                                                QueueContentDialog dialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                };

                                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                                            }
                                            catch (InvalidOperationException)
                                            {
                                                QueueContentDialog dialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                                    PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                };

                                                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                {
                                                    if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                    {
                                                        goto Retry;
                                                    }
                                                    else
                                                    {
                                                        QueueContentDialog ErrorDialog = new QueueContentDialog
                                                        {
                                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                            Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                        };

                                                        _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                    }
                                                }
                                            }
                                            catch (Exception)
                                            {
                                                QueueContentDialog dialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_MoveFailUnexpectError_Content"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                };

                                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                                            }

                                            break;
                                        }
                                }
                            }
                        }
                    }

                    if (e.DataView.Contains(StandardDataFormats.StorageItems))
                    {
                        List<IStorageItem> DragItemList = (await e.DataView.GetStorageItemsAsync()).ToList();

                        if ((sender as SelectorItem).Content is FileSystemStorageItemBase Item)
                        {
                            StorageFolder TargetFolder = (await Item.GetStorageItem().ConfigureAwait(true)) as StorageFolder;

                            if (DragItemList.Contains(TargetFolder))
                            {
                                QueueContentDialog Dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_DragIncludeFolderError"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };
                                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                                return;
                            }

                            switch (e.AcceptedOperation)
                            {
                                case DataPackageOperation.Copy:
                                    {
                                        await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                                    Retry:
                                        try
                                        {
                                            await FullTrustProcessController.Current.CopyAsync(DragItemList, TargetFolder, (s, arg) =>
                                            {
                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                {
                                                    Container.ProBar.IsIndeterminate = false;
                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                }
                                            }).ConfigureAwait(true);

                                            if (!SettingControl.IsDetachTreeViewAndPresenter)
                                            {
                                                await Container.FolderTree.RootNodes[0].UpdateAllSubNodeAsync().ConfigureAwait(true);
                                            }
                                        }
                                        catch (FileNotFoundException)
                                        {
                                            QueueContentDialog Dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_CopyFailForNotExist_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                        }
                                        catch (InvalidOperationException)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                            };

                                            if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                            {
                                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                {
                                                    goto Retry;
                                                }
                                                else
                                                {
                                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                }
                                            }
                                        }
                                        catch (Exception)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                                        }

                                        break;
                                    }
                                case DataPackageOperation.Move:
                                    {
                                        await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Moving")).ConfigureAwait(true);

                                    Retry:
                                        try
                                        {
                                            await FullTrustProcessController.Current.MoveAsync(DragItemList, TargetFolder, (s, arg) =>
                                            {
                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                {
                                                    Container.ProBar.IsIndeterminate = false;
                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                }
                                            }).ConfigureAwait(true);
                                        }
                                        catch (FileNotFoundException)
                                        {
                                            QueueContentDialog Dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_MoveFailForNotExist_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                        }
                                        catch (FileCaputureException)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                                        }
                                        catch (InvalidOperationException)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                            };

                                            if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                            {
                                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                {
                                                    goto Retry;
                                                }
                                                else
                                                {
                                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                }
                                            }
                                        }
                                        catch (Exception)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_MoveFailUnexpectError_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                                        }

                                        break;
                                    }
                            }
                        }
                    }
                }
                catch
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_FailToGetClipboardError_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await dialog.ShowAsync().ConfigureAwait(true);
                }
                finally
                {
                    e.Handled = true;
                    Deferral.Complete();
                    await Container.LoadingActivation(false).ConfigureAwait(true);

                    _ = Interlocked.Exchange(ref DropLock, 0);
                }
            }
        }


        private async void ViewControl_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            if (e.Items.Count != 0)
            {
                List<IStorageItem> TempList = new List<IStorageItem>(e.Items.Count);
                List<FileSystemStorageItemBase> DragList = e.Items.Select((Item) => Item as FileSystemStorageItemBase).ToList();

                foreach (FileSystemStorageItemBase StorageItem in DragList.Where((Item) => !(Item is HyperlinkStorageItem || Item is HiddenStorageItem)))
                {
                    if (ItemPresenter.ContainerFromItem(StorageItem) is SelectorItem SItem && SItem.ContentTemplateRoot.FindChildOfType<TextBox>() is TextBox NameEditBox)
                    {
                        NameEditBox.Visibility = Visibility.Collapsed;
                    }

                    if (await StorageItem.GetStorageItem().ConfigureAwait(true) is IStorageItem Item)
                    {
                        TempList.Add(Item);
                    }
                }

                if (TempList.Count > 0)
                {
                    e.Data.SetStorageItems(TempList, false);
                }

                List<FileSystemStorageItemBase> NotStorageItems = DragList.Where((Item) => Item is HyperlinkStorageItem || Item is HiddenStorageItem).ToList();
                if (NotStorageItems.Count > 0)
                {
                    StringBuilder Builder = new StringBuilder("<head>RX-Explorer-TransferNotStorageItem</head>");

                    foreach (FileSystemStorageItemBase Item in NotStorageItems)
                    {
                        if (ItemPresenter.ContainerFromItem(Item) is SelectorItem SItem && SItem.ContentTemplateRoot.FindChildOfType<TextBox>() is TextBox NameEditBox)
                        {
                            NameEditBox.Visibility = Visibility.Collapsed;
                        }

                        Builder.Append($"<p>{Item.Path}</p>");
                    }

                    e.Data.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(Builder.ToString()));
                }
            }
        }

        private void ViewControl_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            args.ItemContainer.UseSystemFocusVisuals = false;

            if (args.Item is FileSystemStorageItemBase Item)
            {
                if (Item.StorageType == StorageItemTypes.Folder)
                {
                    args.ItemContainer.AllowDrop = true;
                    args.ItemContainer.Drop += Item_Drop;
                    args.ItemContainer.DragEnter += ItemContainer_DragEnter;
                }

                if (Item is HiddenStorageItem)
                {
                    args.ItemContainer.AllowDrop = false;
                    args.ItemContainer.CanDrag = false;
                }

                args.ItemContainer.PointerEntered += ItemContainer_PointerEntered;

                args.RegisterUpdateCallback(async (s, e) =>
                {
                    if (e.Item is FileSystemStorageItemBase Item)
                    {
                        await Item.LoadMoreProperty().ConfigureAwait(false);
                    }
                });
            }
        }

        private void ItemContainer_DragEnter(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                if (sender is SelectorItem)
                {
                    FileSystemStorageItemBase Item = (sender as SelectorItem).Content as FileSystemStorageItemBase;

                    if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                    {
                        e.AcceptedOperation = DataPackageOperation.Copy;
                        e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_CopyTo")} {Item.Name}";
                    }
                    else
                    {
                        e.AcceptedOperation = DataPackageOperation.Move;
                        e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_MoveTo")} {Item.Name}";
                    }

                    e.DragUIOverride.IsContentVisible = true;
                    e.DragUIOverride.IsCaptionVisible = true;
                }
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        private void ItemContainer_PointerEntered(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!SettingControl.IsDoubleClickEnable && ItemPresenter.SelectionMode != ListViewSelectionMode.Multiple && e.KeyModifiers != VirtualKeyModifiers.Control && e.KeyModifiers != VirtualKeyModifiers.Shift)
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Item)
                {
                    SelectedItem = Item;
                }
            }
        }

        private async void ViewControl_Drop(object sender, DragEventArgs e)
        {
            DragOperationDeferral Deferral = e.GetDeferral();

            if (Interlocked.Exchange(ref ViewDropLock, 1) == 0)
            {
                try
                {
                    if (e.DataView.Contains(StandardDataFormats.Html))
                    {
                        string Html = await e.DataView.GetHtmlFormatAsync();
                        string Fragment = HtmlFormatHelper.GetStaticFragment(Html);

                        HtmlDocument Document = new HtmlDocument();
                        Document.LoadHtml(Fragment);
                        HtmlNode HeadNode = Document.DocumentNode.SelectSingleNode("/head");

                        if (HeadNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                        {
                            HtmlNodeCollection BodyNode = Document.DocumentNode.SelectNodes("/p");
                            List<string> LinkItemsPath = BodyNode.Select((Node) => Node.InnerText).ToList();

                            StorageFolder TargetFolder = Container.CurrentFolder;

                            switch (e.AcceptedOperation)
                            {
                                case DataPackageOperation.Copy:
                                    {
                                        await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                                    Retry:
                                        try
                                        {
                                            await FullTrustProcessController.Current.CopyAsync(LinkItemsPath, TargetFolder.Path, (s, arg) =>
                                            {
                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                {
                                                    Container.ProBar.IsIndeterminate = false;
                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                }
                                            }).ConfigureAwait(true);
                                        }
                                        catch (FileNotFoundException)
                                        {
                                            QueueContentDialog Dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_CopyFailForNotExist_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                        }
                                        catch (InvalidOperationException)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                            };

                                            if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                            {
                                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                {
                                                    goto Retry;
                                                }
                                                else
                                                {
                                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                }
                                            }
                                        }
                                        catch (Exception)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                                        }

                                        break;
                                    }
                                case DataPackageOperation.Move:
                                    {
                                        if (LinkItemsPath.All((Item) => Path.GetDirectoryName(Item) == TargetFolder.Path))
                                        {
                                            return;
                                        }

                                        await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Moving")).ConfigureAwait(true);

                                    Retry:
                                        try
                                        {
                                            await FullTrustProcessController.Current.MoveAsync(LinkItemsPath, TargetFolder.Path, (s, arg) =>
                                            {
                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                {
                                                    Container.ProBar.IsIndeterminate = false;
                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                }
                                            }).ConfigureAwait(true);
                                        }
                                        catch (FileNotFoundException)
                                        {
                                            QueueContentDialog Dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_MoveFailForNotExist_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                        }
                                        catch (FileCaputureException)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                                        }
                                        catch (InvalidOperationException)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                                PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                            };

                                            if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                            {
                                                if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                                {
                                                    goto Retry;
                                                }
                                                else
                                                {
                                                    QueueContentDialog ErrorDialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                                }
                                            }
                                        }
                                        catch (Exception)
                                        {
                                            QueueContentDialog dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = Globalization.GetString("QueueDialog_MoveFailUnexpectError_Content"),
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                                        }

                                        break;
                                    }
                            }
                        }
                    }

                    if (e.DataView.Contains(StandardDataFormats.StorageItems))
                    {
                        List<IStorageItem> DragItemList = (await e.DataView.GetStorageItemsAsync()).ToList();

                        StorageFolder TargetFolder = Container.CurrentFolder;

                        if (DragItemList.Contains(TargetFolder))
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_DragIncludeFolderError"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };
                            _ = await Dialog.ShowAsync().ConfigureAwait(true);

                            return;
                        }

                        switch (e.AcceptedOperation)
                        {
                            case DataPackageOperation.Copy:
                                {
                                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                                Retry:
                                    try
                                    {
                                        await FullTrustProcessController.Current.CopyAsync(DragItemList, TargetFolder, (s, arg) =>
                                        {
                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = arg.ProgressPercentage;
                                            }
                                        }).ConfigureAwait(true);
                                    }
                                    catch (FileNotFoundException)
                                    {
                                        QueueContentDialog Dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_CopyFailForNotExist_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                    catch (InvalidOperationException)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                            PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                        };

                                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                        {
                                            if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                            {
                                                goto Retry;
                                            }
                                            else
                                            {
                                                QueueContentDialog ErrorDialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                };

                                                _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                            }
                                        }
                                    }
                                    catch (Exception)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                                    }

                                    break;
                                }
                            case DataPackageOperation.Move:
                                {
                                    if (DragItemList.Select((Item) => Item.Path).All((Item) => Path.GetDirectoryName(Item) == TargetFolder.Path))
                                    {
                                        return;
                                    }

                                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Moving")).ConfigureAwait(true);

                                Retry:
                                    try
                                    {
                                        await FullTrustProcessController.Current.MoveAsync(DragItemList, TargetFolder, (s, arg) =>
                                        {
                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = arg.ProgressPercentage;
                                            }
                                        }).ConfigureAwait(true);
                                    }
                                    catch (FileCaputureException)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                    catch (FileNotFoundException)
                                    {
                                        QueueContentDialog Dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_MoveFailForNotExist_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                    catch (InvalidOperationException)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                            PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                        };

                                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                        {
                                            if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                                            {
                                                goto Retry;
                                            }
                                            else
                                            {
                                                QueueContentDialog ErrorDialog = new QueueContentDialog
                                                {
                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                    Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                };

                                                _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                                            }
                                        }
                                    }
                                    catch (Exception)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_MoveFailUnexpectError_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                                    }

                                    break;
                                }
                        }
                    }
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_DropFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
                finally
                {
                    e.Handled = true;
                    Deferral.Complete();
                    await Container.LoadingActivation(false).ConfigureAwait(true);
                    _ = Interlocked.Exchange(ref ViewDropLock, 0);
                }
            }
        }

        private async void ViewControl_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == Windows.UI.Input.HoldingState.Started)
            {
                e.Handled = true;

                if (ItemPresenter is GridView)
                {
                    if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Context)
                    {
                        if (SelectedItems.Count > 1 && SelectedItems.Contains(Context))
                        {
                            if (SelectedItems.Any((Item) => Item is HiddenStorageItem))
                            {
                                MixZip.IsEnabled = false;
                            }
                            else
                            {
                                MixZip.IsEnabled = true;
                            }

                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(MixedFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                        }
                        else
                        {
                            SelectedItem = Context;

                            if (Context is HiddenStorageItem)
                            {
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(HiddenItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                            else if (Context is HyperlinkStorageItem)
                            {
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                            else
                            {
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(Context.StorageType == StorageItemTypes.Folder ? FolderFlyout : FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                        }
                    }
                    else
                    {
                        SelectedItem = null;

                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                    }
                }
                else
                {
                    if (e.OriginalSource is FrameworkElement Element)
                    {
                        if (Element.Name == "EmptyTextblock")
                        {
                            SelectedItem = null;
                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                        }
                        else
                        {
                            if (Element.DataContext is FileSystemStorageItemBase Context)
                            {
                                if (SelectedItems.Count > 1 && SelectedItems.Contains(Context))
                                {
                                    if (SelectedItems.Any((Item) => Item is HiddenStorageItem))
                                    {
                                        MixZip.IsEnabled = false;
                                    }
                                    else
                                    {
                                        MixZip.IsEnabled = true;
                                    }

                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(MixedFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                }
                                else
                                {
                                    if (SelectedItem == Context)
                                    {
                                        if (Context is HiddenStorageItem)
                                        {
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(HiddenItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                        else if (Context is HyperlinkStorageItem)
                                        {
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                        else
                                        {
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(Context.StorageType == StorageItemTypes.Folder ? FolderFlyout : FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                    }
                                    else
                                    {
                                        if (e.OriginalSource is TextBlock)
                                        {
                                            SelectedItem = Context;

                                            if (Context is HiddenStorageItem)
                                            {
                                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(HiddenItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                            }
                                            else if (Context is HyperlinkStorageItem)
                                            {
                                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                            }
                                            else
                                            {
                                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(Context.StorageType == StorageItemTypes.Folder ? FolderFlyout : FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                            }
                                        }
                                        else
                                        {
                                            SelectedItem = null;
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                SelectedItem = null;
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                        }
                    }
                }
            }
        }

        private async void MixZip_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItems.Any((Item) => Item is HyperlinkStorageItem))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LinkIsNotAllowInMixZip_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                return;
            }

            foreach (FileSystemStorageItemBase Item in SelectedItems)
            {
                if (Item.StorageType == StorageItemTypes.Folder)
                {
                    StorageFolder Folder = (await Item.GetStorageItem().ConfigureAwait(true)) as StorageFolder;

                    if (!WIN_Native_API.CheckExist(Folder.Path))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                        };
                        _ = await Dialog.ShowAsync().ConfigureAwait(true);

                        await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    StorageFile File = (await Item.GetStorageItem().ConfigureAwait(true)) as StorageFile;

                    if (!WIN_Native_API.CheckExist(File.Path))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                        };
                        _ = await Dialog.ShowAsync().ConfigureAwait(true);

                        await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(false);
                        return;
                    }
                }
            }

            bool IsCompress = false;
            if (SelectedItems.All((Item) => Item.StorageType == StorageItemTypes.File))
            {
                if (SelectedItems.All((Item) => Item.Type == ".zip"))
                {
                    IsCompress = false;
                }
                else if (SelectedItems.All((Item) => Item.Type != ".zip"))
                {
                    IsCompress = true;
                }
                else
                {
                    return;
                }
            }
            else if (SelectedItems.All((Item) => Item.StorageType == StorageItemTypes.Folder))
            {
                IsCompress = true;
            }
            else
            {
                if (SelectedItems.Where((It) => It.StorageType == StorageItemTypes.File).All((Item) => Item.Type != ".zip"))
                {
                    IsCompress = true;
                }
                else
                {
                    return;
                }
            }

            if (IsCompress)
            {
                ZipDialog dialog = new ZipDialog(Globalization.GetString("Zip_Admin_Name_Text"));

                if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                {
                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Compressing")).ConfigureAwait(true);

                    await CreateZipAsync(SelectedItems, dialog.FileName, (int)dialog.Level, ProgressHandler: (s, e) =>
                    {
                        if (Container.ProBar.Value < e.ProgressPercentage)
                        {
                            Container.ProBar.IsIndeterminate = false;
                            Container.ProBar.Value = e.ProgressPercentage;
                        }
                    }).ConfigureAwait(true);

                    await Container.LoadingActivation(false).ConfigureAwait(true);
                }
            }
            else
            {
                await UnZipAsync(SelectedItems, (s, e) =>
                {
                    if (Container.ProBar.Value < e.ProgressPercentage)
                    {
                        Container.ProBar.IsIndeterminate = false;
                        Container.ProBar.Value = e.ProgressPercentage;
                    }
                }).ConfigureAwait(true);
            }
        }

        private async void TryUnlock_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItem is FileSystemStorageItemBase Item && Item.StorageType == StorageItemTypes.File)
            {
                try
                {
                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Unlock")).ConfigureAwait(true);

                    if (await FullTrustProcessController.Current.TryUnlockFileOccupy(Item.Path).ConfigureAwait(true))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_TipTitle"),
                            Content = Globalization.GetString("QueueDialog_Unlock_Success_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };
                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                    }
                    else
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_TipTitle"),
                            Content = Globalization.GetString("QueueDialog_Unlock_Failure_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };
                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                    }
                }
                catch (FileNotFoundException)
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_TipTitle"),
                        Content = Globalization.GetString("QueueDialog_Unlock_FileNotFound_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                }
                catch (UnlockException)
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_TipTitle"),
                        Content = Globalization.GetString("QueueDialog_Unlock_NoLock_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_TipTitle"),
                        Content = Globalization.GetString("QueueDialog_Unlock_UnexpectedError_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
                finally
                {
                    await Container.LoadingActivation(false).ConfigureAwait(false);
                }
            }
        }

        private async void CalculateHash_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            try
            {
                if (HashTeachTip.IsOpen)
                {
                    HashTeachTip.IsOpen = false;
                }

                await Task.Run(() =>
                {
                    SpinWait.SpinUntil(() => HashCancellation == null);
                }).ConfigureAwait(true);

                if ((await SelectedItem.GetStorageItem().ConfigureAwait(true)) is StorageFile Item)
                {
                    Hash_Crc32.IsEnabled = false;
                    Hash_SHA1.IsEnabled = false;
                    Hash_SHA256.IsEnabled = false;
                    Hash_MD5.IsEnabled = false;

                    Hash_Crc32.Text = string.Empty;
                    Hash_SHA1.Text = string.Empty;
                    Hash_SHA256.Text = string.Empty;
                    Hash_MD5.Text = string.Empty;

                    await Task.Delay(500).ConfigureAwait(true);
                    HashTeachTip.Target = ItemPresenter.ContainerFromItem(SelectedItem) as FrameworkElement;
                    HashTeachTip.IsOpen = true;

                    using (HashCancellation = new CancellationTokenSource())
                    {
                        Task<string> task1 = Item.ComputeSHA256Hash(HashCancellation.Token);
                        Hash_SHA256.IsEnabled = true;

                        Task<string> task2 = Item.ComputeCrc32Hash(HashCancellation.Token);
                        Hash_Crc32.IsEnabled = true;

                        Task<string> task4 = Item.ComputeMD5Hash(HashCancellation.Token);
                        Hash_MD5.IsEnabled = true;

                        Task<string> task3 = Item.ComputeSHA1Hash(HashCancellation.Token);
                        Hash_SHA1.IsEnabled = true;

                        Hash_MD5.Text = await task4.ConfigureAwait(true);
                        Hash_Crc32.Text = await task2.ConfigureAwait(true);
                        Hash_SHA1.Text = await task3.ConfigureAwait(true);
                        Hash_SHA256.Text = await task1.ConfigureAwait(true);
                    }
                }
                else
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, "Error: CalculateHash failed");
            }
            finally
            {
                HashCancellation = null;
            }
        }

        private void Hash_Crc32_Copy_Click(object sender, RoutedEventArgs e)
        {
            DataPackage Package = new DataPackage();
            Package.SetText(Hash_Crc32.Text);
            Clipboard.SetContent(Package);
        }

        private void Hash_SHA1_Copy_Click(object sender, RoutedEventArgs e)
        {
            DataPackage Package = new DataPackage();
            Package.SetText(Hash_SHA1.Text);
            Clipboard.SetContent(Package);
        }

        private void Hash_SHA256_Copy_Click(object sender, RoutedEventArgs e)
        {
            DataPackage Package = new DataPackage();
            Package.SetText(Hash_SHA256.Text);
            Clipboard.SetContent(Package);
        }

        private void Hash_MD5_Copy_Click(object sender, RoutedEventArgs e)
        {
            DataPackage Package = new DataPackage();
            Package.SetText(Hash_MD5.Text);
            Clipboard.SetContent(Package);
        }

        private void HashTeachTip_Closing(TeachingTip sender, TeachingTipClosingEventArgs args)
        {
            HashCancellation?.Cancel();
        }

        private async void OpenInTerminal_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (await SQLite.Current.GetTerminalProfileByName(Convert.ToString(ApplicationData.Current.LocalSettings.Values["DefaultTerminal"])).ConfigureAwait(true) is TerminalProfile Profile)
            {
            Retry:
                try
                {
                    await FullTrustProcessController.Current.RunAsync(Profile.Path, Profile.RunAsAdmin, false, Regex.Matches(Profile.Argument, "[^ \"]+|\"[^\"]*\"").Select((Mat) => Mat.Value == "[CurrentLocation]" ? Container.CurrentFolder.Path : Mat.Value).ToArray()).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedExecute_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                    };

                    if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                        {
                            goto Retry;
                        }
                        else
                        {
                            QueueContentDialog ErrorDialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                        }
                    }
                }
            }
        }

        private async void OpenFolderInNewTab_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItem is FileSystemStorageItemBase Item && Item.StorageType == StorageItemTypes.Folder)
            {
                await TabViewContainer.ThisPage.CreateNewTabAndOpenTargetFolder(Item.Path).ConfigureAwait(false);
            }
        }

        private void NameLabel_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            TextBlock NameLabel = (TextBlock)sender;

            if ((e.GetCurrentPoint(NameLabel).Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) && SettingControl.IsDoubleClickEnable)
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Item)
                {
                    if (Item is HiddenStorageItem)
                    {
                        return;
                    }

                    if (SelectedItem == Item)
                    {
                        TimeSpan ClickSpan = DateTimeOffset.Now - LastClickTime;

                        if (ClickSpan.TotalMilliseconds > 1000 && ClickSpan.TotalMilliseconds < 3000)
                        {
                            NameLabel.Visibility = Visibility.Collapsed;
                            CurrentNameEditItem = Item;

                            if ((NameLabel.Parent as FrameworkElement).FindName("NameEditBox") is TextBox EditBox)
                            {
                                EditBox.Text = NameLabel.Text;
                                EditBox.Visibility = Visibility.Visible;
                                EditBox.Focus(FocusState.Programmatic);
                            }

                            MainPage.ThisPage.IsAnyTaskRunning = true;
                        }
                    }

                    LastClickTime = DateTimeOffset.Now;
                }
            }
        }

        private async void NameEditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox NameEditBox = (TextBox)sender;

            if ((NameEditBox?.Parent as FrameworkElement)?.FindName("NameLabel") is TextBlock NameLabel && CurrentNameEditItem != null)
            {
                try
                {
                    if (!FileSystemItemNameChecker.IsValid(NameEditBox.Text))
                    {
                        InvalidNameTip.Target = NameLabel;
                        InvalidNameTip.IsOpen = true;
                        return;
                    }

                    if (CurrentNameEditItem.Name == NameEditBox.Text)
                    {
                        return;
                    }

                    if (WIN_Native_API.CheckExist(Path.Combine(Path.GetDirectoryName(CurrentNameEditItem.Path), NameEditBox.Text)))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_RenameExist_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ContinueButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                        };

                        if (await Dialog.ShowAsync().ConfigureAwait(true) != ContentDialogResult.Primary)
                        {
                            return;
                        }
                    }

                Retry:
                    try
                    {
                        await FullTrustProcessController.Current.RenameAsync(CurrentNameEditItem.Path, NameEditBox.Text).ConfigureAwait(true);
                    }
                    catch (FileLoadException)
                    {
                        QueueContentDialog LoadExceptionDialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_FileOccupied_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                        };

                        _ = await LoadExceptionDialog.ShowAsync().ConfigureAwait(true);
                    }
                    catch (InvalidOperationException)
                    {
                        QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnauthorizedRenameFile_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_GrantButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                        };

                        if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                        {
                            if (await FullTrustProcessController.Current.SwitchToAdminMode().ConfigureAwait(true))
                            {
                                goto Retry;
                            }
                            else
                            {
                                QueueContentDialog ErrorDialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_DenyElevation_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await ErrorDialog.ShowAsync().ConfigureAwait(true);
                            }
                        }
                    }
                }
                catch
                {
                    QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedRename_StartExplorer_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        _ = await Launcher.LaunchFolderAsync(Container.CurrentFolder);
                    }
                }
                finally
                {
                    NameEditBox.Visibility = Visibility.Collapsed;

                    NameLabel.Visibility = Visibility.Visible;

                    LastClickTime = DateTimeOffset.MaxValue;

                    MainPage.ThisPage.IsAnyTaskRunning = false;
                }
            }
        }

        private void GetFocus_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ItemPresenter.Focus(FocusState.Programmatic);
        }

        private async void OpenFolderInNewWindow_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (SelectedItem is FileSystemStorageItemBase Item && Item.StorageType == StorageItemTypes.Folder)
            {
                await Launcher.LaunchUriAsync(new Uri($"rx-explorer:{Uri.EscapeDataString(Item.Path)}"));
            }
        }

        private async void Undo_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            await Ctrl_Z_Click().ConfigureAwait(false);
        }

        private async void RemoveHidden_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (await FullTrustProcessController.Current.RemoveHiddenAttribute(SelectedItem.Path).ConfigureAwait(true))
            {
                if (WIN_Native_API.GetStorageItems(SelectedItem.Path).FirstOrDefault() is FileSystemStorageItemBase Item)
                {
                    int Index = FileCollection.IndexOf(SelectedItem);

                    if (Index != -1)
                    {
                        FileCollection.Remove(SelectedItem);
                        FileCollection.Insert(Index, Item);
                    }
                    else
                    {
                        FileCollection.Add(Item);
                    }

                    ItemPresenter.UpdateLayout();

                    SelectedItem = Item;
                }
            }
            else
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_RemoveHiddenError_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(false);
            }
        }

        private async void OpenHiddenItemExplorer_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            if (!await Launcher.LaunchFolderPathAsync(SelectedItem.Path))
            {
                await Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(SelectedItem.Path));
            }
        }

        private void NameEditBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
        {
            if (args.NewText.Any((Item) => Path.GetInvalidFileNameChars().Contains(Item)))
            {
                args.Cancel = true;

                if ((sender.Parent as FrameworkElement).FindName("NameLabel") is TextBlock NameLabel)
                {
                    InvalidCharTip.Target = NameLabel;
                    InvalidCharTip.IsOpen = true;
                }
            }
        }

        private void OrderByName_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            SortCollectionGenerator.Current.ModifySortWay(SortTarget.Name, Desc.IsChecked ? SortDirection.Descending : SortDirection.Ascending);

            List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

            FileCollection.Clear();

            foreach (FileSystemStorageItemBase Item in SortResult)
            {
                FileCollection.Add(Item);
            }
        }

        private void OrderByTime_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            SortCollectionGenerator.Current.ModifySortWay(SortTarget.ModifiedTime, Desc.IsChecked ? SortDirection.Descending : SortDirection.Ascending);

            List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

            FileCollection.Clear();

            foreach (FileSystemStorageItemBase Item in SortResult)
            {
                FileCollection.Add(Item);
            }
        }

        private void OrderByType_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            SortCollectionGenerator.Current.ModifySortWay(SortTarget.Type, Desc.IsChecked ? SortDirection.Descending : SortDirection.Ascending);

            List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

            FileCollection.Clear();

            foreach (FileSystemStorageItemBase Item in SortResult)
            {
                FileCollection.Add(Item);
            }
        }

        private void OrderBySize_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            SortCollectionGenerator.Current.ModifySortWay(SortTarget.Size, Desc.IsChecked ? SortDirection.Descending : SortDirection.Ascending);

            List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

            FileCollection.Clear();

            foreach (FileSystemStorageItemBase Item in SortResult)
            {
                FileCollection.Add(Item);
            }
        }

        private void Desc_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            SortCollectionGenerator.Current.ModifySortWay(SortDirection: SortDirection.Descending);

            List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

            FileCollection.Clear();

            foreach (FileSystemStorageItemBase Item in SortResult)
            {
                FileCollection.Add(Item);
            }
        }

        private void Asc_Click(object sender, RoutedEventArgs e)
        {
            Restore();

            SortCollectionGenerator.Current.ModifySortWay(SortDirection: SortDirection.Ascending);

            List<FileSystemStorageItemBase> SortResult = SortCollectionGenerator.Current.GetSortedCollection(FileCollection);

            FileCollection.Clear();

            foreach (FileSystemStorageItemBase Item in SortResult)
            {
                FileCollection.Add(Item);
            }
        }

        private void SortMenuFlyout_Opening(object sender, object e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                Desc.IsChecked = false;
                Asc.IsChecked = true;
            }
            else
            {
                Asc.IsChecked = false;
                Desc.IsChecked = true;
            }

            switch (SortCollectionGenerator.Current.SortTarget)
            {
                case SortTarget.Name:
                    {
                        OrderByType.IsChecked = false;
                        OrderByTime.IsChecked = false;
                        OrderBySize.IsChecked = false;
                        OrderByName.IsChecked = true;
                        break;
                    }
                case SortTarget.Type:
                    {
                        OrderByTime.IsChecked = false;
                        OrderBySize.IsChecked = false;
                        OrderByName.IsChecked = false;
                        OrderByType.IsChecked = true;
                        break;
                    }
                case SortTarget.ModifiedTime:
                    {
                        OrderBySize.IsChecked = false;
                        OrderByName.IsChecked = false;
                        OrderByType.IsChecked = false;
                        OrderByTime.IsChecked = true;
                        break;
                    }
                case SortTarget.Size:
                    {
                        OrderByName.IsChecked = false;
                        OrderByType.IsChecked = false;
                        OrderByTime.IsChecked = false;
                        OrderBySize.IsChecked = true;
                        break;
                    }
            }
        }

        private async void BottomCommandBar_Opening(object sender, object e)
        {
            BottomCommandBar.PrimaryCommands.Clear();
            BottomCommandBar.SecondaryCommands.Clear();

            if (SelectedItems.Count > 1)
            {
                AppBarButton CopyButton = new AppBarButton
                {
                    Icon = new SymbolIcon(Symbol.Copy),
                    Label = Globalization.GetString("Operate_Text_Copy")
                };
                CopyButton.Click += Copy_Click;
                BottomCommandBar.PrimaryCommands.Add(CopyButton);

                AppBarButton CutButton = new AppBarButton
                {
                    Icon = new SymbolIcon(Symbol.Cut),
                    Label = Globalization.GetString("Operate_Text_Cut")
                };
                CutButton.Click += Cut_Click;
                BottomCommandBar.PrimaryCommands.Add(CutButton);

                AppBarButton DeleteButton = new AppBarButton
                {
                    Icon = new SymbolIcon(Symbol.Delete),
                    Label = Globalization.GetString("Operate_Text_Delete")
                };
                DeleteButton.Click += Delete_Click;
                BottomCommandBar.PrimaryCommands.Add(DeleteButton);

                bool EnableMixZipButton = true;
                string MixZipButtonText = Globalization.GetString("Operate_Text_Compression");

                if (SelectedItems.Any((Item) => Item is HiddenStorageItem))
                {
                    EnableMixZipButton = false;
                }
                else
                {
                    if (SelectedItems.Any((Item) => Item.StorageType != StorageItemTypes.Folder))
                    {
                        if (SelectedItems.All((Item) => Item.StorageType == StorageItemTypes.File))
                        {
                            if (SelectedItems.All((Item) => Item.Type == ".zip"))
                            {
                                MixZipButtonText = Globalization.GetString("Operate_Text_Decompression");
                            }
                            else if (SelectedItems.All((Item) => Item.Type != ".zip"))
                            {
                                MixZipButtonText = Globalization.GetString("Operate_Text_Compression");
                            }
                            else
                            {
                                EnableMixZipButton = false;
                            }
                        }
                        else
                        {
                            if (SelectedItems.Where((It) => It.StorageType == StorageItemTypes.File).Any((Item) => Item.Type == ".zip"))
                            {
                                EnableMixZipButton = false;
                            }
                            else
                            {
                                MixZipButtonText = Globalization.GetString("Operate_Text_Compression");
                            }
                        }
                    }
                    else
                    {
                        MixZipButtonText = Globalization.GetString("Operate_Text_Compression");
                    }
                }

                AppBarButton CompressionButton = new AppBarButton
                {
                    Icon = new SymbolIcon(Symbol.Bookmarks),
                    Label = MixZipButtonText,
                    IsEnabled = EnableMixZipButton
                };
                CompressionButton.Click += MixZip_Click;
                BottomCommandBar.SecondaryCommands.Add(CompressionButton);
            }
            else
            {
                if (SelectedItem is FileSystemStorageItemBase Item)
                {
                    if (Item is HiddenStorageItem)
                    {
                        AppBarButton CopyButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Copy),
                            Label = Globalization.GetString("Operate_Text_Copy")
                        };
                        CopyButton.Click += Copy_Click;
                        BottomCommandBar.PrimaryCommands.Add(CopyButton);

                        AppBarButton CutButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Cut),
                            Label = Globalization.GetString("Operate_Text_Cut")
                        };
                        CutButton.Click += Cut_Click;
                        BottomCommandBar.PrimaryCommands.Add(CutButton);

                        AppBarButton DeleteButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Delete),
                            Label = Globalization.GetString("Operate_Text_Delete")
                        };
                        DeleteButton.Click += Delete_Click;
                        BottomCommandBar.PrimaryCommands.Add(DeleteButton);

                        AppBarButton WinExButton = new AppBarButton
                        {
                            Icon = new FontIcon { Glyph = "\uEC50" },
                            Label = Globalization.GetString("Operate_Text_OpenInWinExplorer")
                        };
                        WinExButton.Click += OpenHiddenItemExplorer_Click;
                        BottomCommandBar.PrimaryCommands.Add(WinExButton);

                        AppBarButton RemoveHiddenButton = new AppBarButton
                        {
                            Icon = new FontIcon { Glyph = "\uF5EF" },
                            Label = Globalization.GetString("Operate_Text_RemoveHidden")
                        };
                        RemoveHiddenButton.Click += RemoveHidden_Click;
                        BottomCommandBar.PrimaryCommands.Add(RemoveHiddenButton);
                    }
                    else
                    {
                        AppBarButton CopyButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Copy),
                            Label = Globalization.GetString("Operate_Text_Copy")
                        };
                        CopyButton.Click += Copy_Click;
                        BottomCommandBar.PrimaryCommands.Add(CopyButton);

                        AppBarButton CutButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Cut),
                            Label = Globalization.GetString("Operate_Text_Cut")
                        };
                        CutButton.Click += Cut_Click;
                        BottomCommandBar.PrimaryCommands.Add(CutButton);

                        AppBarButton DeleteButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Delete),
                            Label = Globalization.GetString("Operate_Text_Delete")
                        };
                        DeleteButton.Click += Delete_Click;
                        BottomCommandBar.PrimaryCommands.Add(DeleteButton);

                        AppBarButton RenameButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Rename),
                            Label = Globalization.GetString("Operate_Text_Rename")
                        };
                        RenameButton.Click += Rename_Click;
                        BottomCommandBar.PrimaryCommands.Add(RenameButton);

                        if (Item.StorageType == StorageItemTypes.File)
                        {
                            AppBarButton OpenButton = new AppBarButton
                            {
                                Icon = new SymbolIcon(Symbol.OpenFile),
                                Label = Globalization.GetString("Operate_Text_Open")
                            };
                            OpenButton.Click += ItemOpen_Click;
                            BottomCommandBar.SecondaryCommands.Add(OpenButton);

                            MenuFlyout OpenFlyout = new MenuFlyout();
                            MenuFlyoutItem AdminItem = new MenuFlyoutItem
                            {
                                Icon = new FontIcon { Glyph = "\uEA0D" },
                                Text = Globalization.GetString("Operate_Text_OpenAsAdministrator"),
                                IsEnabled = RunWithSystemAuthority.IsEnabled
                            };
                            AdminItem.Click += RunWithSystemAuthority_Click;
                            OpenFlyout.Items.Add(AdminItem);

                            MenuFlyoutItem OtherItem = new MenuFlyoutItem
                            {
                                Icon = new SymbolIcon(Symbol.SwitchApps),
                                Text = Globalization.GetString("Operate_Text_ChooseAnotherApp"),
                                IsEnabled = ChooseOtherApp.IsEnabled
                            };
                            OtherItem.Click += ChooseOtherApp_Click;
                            OpenFlyout.Items.Add(OtherItem);

                            BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                            {
                                Icon = new SymbolIcon(Symbol.OpenWith),
                                Label = Globalization.GetString("Operate_Text_OpenWith"),
                                Flyout = OpenFlyout
                            });

                            BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                            MenuFlyout ToolFlyout = new MenuFlyout();
                            MenuFlyoutItem UnLock = new MenuFlyoutItem
                            {
                                Icon = new FontIcon { Glyph = "\uE785" },
                                Text = Globalization.GetString("Operate_Text_Unlock")
                            };
                            UnLock.Click += TryUnlock_Click;
                            ToolFlyout.Items.Add(UnLock);

                            MenuFlyoutItem Hash = new MenuFlyoutItem
                            {
                                Icon = new FontIcon { Glyph = "\uE2B2" },
                                Text = Globalization.GetString("Operate_Text_ComputeHash")
                            };
                            Hash.Click += CalculateHash_Click;
                            ToolFlyout.Items.Add(Hash);

                            BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                            {
                                Icon = new FontIcon { Glyph = "\uE90F" },
                                Label = Globalization.GetString("Operate_Text_Tool"),
                                IsEnabled = FileTool.IsEnabled,
                                Flyout = ToolFlyout
                            });

                            MenuFlyout EditFlyout = new MenuFlyout();
                            MenuFlyoutItem MontageItem = new MenuFlyoutItem
                            {
                                Icon = new FontIcon { Glyph = "\uE177" },
                                Text = Globalization.GetString("Operate_Text_Montage"),
                                IsEnabled = VideoEdit.IsEnabled
                            };
                            MontageItem.Click += VideoEdit_Click;
                            EditFlyout.Items.Add(MontageItem);

                            MenuFlyoutItem MergeItem = new MenuFlyoutItem
                            {
                                Icon = new FontIcon { Glyph = "\uE11E" },
                                Text = Globalization.GetString("Operate_Text_Merge"),
                                IsEnabled = VideoMerge.IsEnabled
                            };
                            MergeItem.Click += VideoMerge_Click;
                            EditFlyout.Items.Add(MergeItem);

                            MenuFlyoutItem TranscodeItem = new MenuFlyoutItem
                            {
                                Icon = new FontIcon { Glyph = "\uE1CA" },
                                Text = Globalization.GetString("Operate_Text_Transcode"),
                                IsEnabled = Transcode.IsEnabled
                            };
                            TranscodeItem.Click += Transcode_Click;
                            EditFlyout.Items.Add(TranscodeItem);

                            BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                            {
                                Icon = new SymbolIcon(Symbol.Edit),
                                Label = Globalization.GetString("Operate_Text_Edit"),
                                IsEnabled = FileEdit.IsEnabled,
                                Flyout = EditFlyout
                            });

                            MenuFlyout ShareFlyout = new MenuFlyout();
                            MenuFlyoutItem SystemShareItem = new MenuFlyoutItem
                            {
                                Icon = new SymbolIcon(Symbol.Share),
                                Text = Globalization.GetString("Operate_Text_SystemShare")
                            };
                            SystemShareItem.Click += SystemShare_Click;
                            ShareFlyout.Items.Add(SystemShareItem);

                            MenuFlyoutItem WIFIShareItem = new MenuFlyoutItem
                            {
                                Icon = new FontIcon { Glyph = "\uE701" },
                                Text = Globalization.GetString("Operate_Text_WIFIShare")
                            };
                            WIFIShareItem.Click += WIFIShare_Click;
                            ShareFlyout.Items.Add(WIFIShareItem);

                            MenuFlyoutItem BluetoothShare = new MenuFlyoutItem
                            {
                                Icon = new FontIcon { Glyph = "\uE702" },
                                Text = Globalization.GetString("Operate_Text_BluetoothShare")
                            };
                            BluetoothShare.Click += BluetoothShare_Click;
                            ShareFlyout.Items.Add(BluetoothShare);

                            BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                            {
                                Icon = new SymbolIcon(Symbol.Share),
                                Label = Globalization.GetString("Operate_Text_Share"),
                                IsEnabled = FileShare.IsEnabled,
                                Flyout = ShareFlyout
                            });

                            BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                            AppBarButton CompressionButton = new AppBarButton
                            {
                                Icon = new SymbolIcon(Symbol.Bookmarks),
                                Label = Zip.Label,
                                IsEnabled = Zip.IsEnabled
                            };
                            CompressionButton.Click += Zip_Click;
                            BottomCommandBar.SecondaryCommands.Add(CompressionButton);

                            BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                            AppBarButton PropertyButton = new AppBarButton
                            {
                                Icon = new SymbolIcon(Symbol.Tag),
                                Label = Globalization.GetString("Operate_Text_Property")
                            };
                            PropertyButton.Click += FileProperty_Click;
                            BottomCommandBar.SecondaryCommands.Add(PropertyButton);
                        }
                        else
                        {
                            AppBarButton OpenButton = new AppBarButton
                            {
                                Icon = new SymbolIcon(Symbol.BackToWindow),
                                Label = Globalization.GetString("Operate_Text_Open")
                            };
                            OpenButton.Click += ItemOpen_Click;
                            BottomCommandBar.SecondaryCommands.Add(OpenButton);

                            AppBarButton NewWindowButton = new AppBarButton
                            {
                                Icon = new FontIcon { Glyph = "\uE727" },
                                Label = Globalization.GetString("Operate_Text_NewWindow")
                            };
                            NewWindowButton.Click += OpenFolderInNewWindow_Click;
                            BottomCommandBar.SecondaryCommands.Add(NewWindowButton);

                            AppBarButton NewTabButton = new AppBarButton
                            {
                                Icon = new FontIcon { Glyph = "\uF7ED" },
                                Label = Globalization.GetString("Operate_Text_NewTab")
                            };
                            NewTabButton.Click += OpenFolderInNewTab_Click;
                            BottomCommandBar.SecondaryCommands.Add(NewTabButton);

                            AppBarButton CompressionButton = new AppBarButton
                            {
                                Icon = new SymbolIcon(Symbol.Bookmarks),
                                Label = Globalization.GetString("Operate_Text_Compression")
                            };
                            CompressionButton.Click += CompressFolder_Click;
                            BottomCommandBar.SecondaryCommands.Add(CompressionButton);

                            BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                            AppBarButton PropertyButton = new AppBarButton
                            {
                                Icon = new SymbolIcon(Symbol.Tag),
                                Label = Globalization.GetString("Operate_Text_Property")
                            };
                            PropertyButton.Click += FolderProperty_Click;
                            BottomCommandBar.SecondaryCommands.Add(PropertyButton);
                        }
                    }
                }
                else
                {
                    bool IsEnablePaste, IsEnableUndo;

                    try
                    {
                        DataPackageView Package = Clipboard.GetContent();

                        if (Package.Contains(StandardDataFormats.StorageItems))
                        {
                            IsEnablePaste = true;
                        }
                        else if (Package.Contains(StandardDataFormats.Html))
                        {
                            string Html = await Package.GetHtmlFormatAsync();
                            string Fragment = HtmlFormatHelper.GetStaticFragment(Html);

                            HtmlDocument Document = new HtmlDocument();
                            Document.LoadHtml(Fragment);
                            HtmlNode HeadNode = Document.DocumentNode.SelectSingleNode("/head");

                            if (HeadNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                            {
                                IsEnablePaste = true;
                            }
                            else
                            {
                                IsEnablePaste = false;
                            }
                        }
                        else
                        {
                            IsEnablePaste = false;
                        }
                    }
                    catch
                    {
                        IsEnablePaste = false;
                    }

                    if (OperationRecorder.Current.Value.Count > 0)
                    {
                        IsEnableUndo = true;
                    }
                    else
                    {
                        IsEnableUndo = false;
                    }

                    AppBarButton MultiSelectButton = new AppBarButton
                    {
                        Icon = new FontIcon { Glyph = "\uE762" },
                        Label = Globalization.GetString("Operate_Text_MultiSelect")
                    };
                    MultiSelectButton.Click += MulSelect_Click;
                    BottomCommandBar.PrimaryCommands.Add(MultiSelectButton);

                    AppBarButton PasteButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Paste),
                        Label = Globalization.GetString("Operate_Text_Paste"),
                        IsEnabled = IsEnablePaste
                    };
                    PasteButton.Click += Paste_Click;
                    BottomCommandBar.PrimaryCommands.Add(PasteButton);

                    AppBarButton UndoButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Undo),
                        Label = Globalization.GetString("Operate_Text_Undo"),
                        IsEnabled = IsEnableUndo
                    };
                    UndoButton.Click += Undo_Click;
                    BottomCommandBar.PrimaryCommands.Add(UndoButton);

                    AppBarButton RefreshButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Refresh),
                        Label = Globalization.GetString("Operate_Text_Refresh")
                    };
                    RefreshButton.Click += Refresh_Click;
                    BottomCommandBar.PrimaryCommands.Add(RefreshButton);

                    MenuFlyout NewFlyout = new MenuFlyout();
                    MenuFlyoutItem CreateFileItem = new MenuFlyoutItem
                    {
                        Icon = new SymbolIcon(Symbol.Page2),
                        Text = Globalization.GetString("Operate_Text_CreateFile"),
                        MinWidth = 150
                    };
                    CreateFileItem.Click += CreateFile_Click;
                    NewFlyout.Items.Add(CreateFileItem);

                    MenuFlyoutItem CreateFolder = new MenuFlyoutItem
                    {
                        Icon = new SymbolIcon(Symbol.NewFolder),
                        Text = Globalization.GetString("Operate_Text_CreateFolder"),
                        MinWidth = 150
                    };
                    CreateFolder.Click += CreateFolder_Click;
                    NewFlyout.Items.Add(CreateFolder);

                    BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Add),
                        Label = Globalization.GetString("Operate_Text_Create"),
                        Flyout = NewFlyout
                    });

                    bool DescCheck = false;
                    bool AscCheck = false;
                    bool NameCheck = false;
                    bool TimeCheck = false;
                    bool TypeCheck = false;
                    bool SizeCheck = false;

                    if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
                    {
                        DescCheck = false;
                        AscCheck = true;
                    }
                    else
                    {
                        AscCheck = false;
                        DescCheck = true;
                    }

                    switch (SortCollectionGenerator.Current.SortTarget)
                    {
                        case SortTarget.Name:
                            {
                                TypeCheck = false;
                                TimeCheck = false;
                                SizeCheck = false;
                                NameCheck = true;
                                break;
                            }
                        case SortTarget.Type:
                            {
                                TimeCheck = false;
                                SizeCheck = false;
                                NameCheck = false;
                                TypeCheck = true;
                                break;
                            }
                        case SortTarget.ModifiedTime:
                            {
                                SizeCheck = false;
                                NameCheck = false;
                                TypeCheck = false;
                                TimeCheck = true;
                                break;
                            }
                        case SortTarget.Size:
                            {
                                NameCheck = false;
                                TypeCheck = false;
                                TimeCheck = false;
                                SizeCheck = true;
                                break;
                            }
                    }

                    MenuFlyout SortFlyout = new MenuFlyout();

                    RadioMenuFlyoutItem SortName = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortTarget_Name"),
                        IsChecked = NameCheck
                    };
                    SortName.Click += OrderByName_Click;
                    SortFlyout.Items.Add(SortName);

                    RadioMenuFlyoutItem SortTime = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortTarget_Time"),
                        IsChecked = TimeCheck
                    };
                    SortTime.Click += OrderByTime_Click;
                    SortFlyout.Items.Add(SortTime);

                    RadioMenuFlyoutItem SortType = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortTarget_Type"),
                        IsChecked = TypeCheck
                    };
                    SortType.Click += OrderByType_Click;
                    SortFlyout.Items.Add(SortType);

                    RadioMenuFlyoutItem SortSize = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortTarget_Size"),
                        IsChecked = SizeCheck
                    };
                    SortSize.Click += OrderBySize_Click;
                    SortFlyout.Items.Add(SortSize);

                    SortFlyout.Items.Add(new MenuFlyoutSeparator());

                    RadioMenuFlyoutItem Asc = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortDirection_Asc"),
                        IsChecked = AscCheck
                    };
                    Asc.Click += Asc_Click;
                    SortFlyout.Items.Add(Asc);

                    RadioMenuFlyoutItem Desc = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortDirection_Desc"),
                        IsChecked = DescCheck
                    };
                    Desc.Click += Desc_Click;
                    SortFlyout.Items.Add(Desc);

                    BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Sort),
                        Label = Globalization.GetString("Operate_Text_Sort"),
                        Flyout = SortFlyout
                    });

                    BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                    AppBarButton WinExButton = new AppBarButton
                    {
                        Icon = new FontIcon { Glyph = "\uEC50" },
                        Label = Globalization.GetString("Operate_Text_OpenInWinExplorer")
                    };
                    WinExButton.Click += UseSystemFileMananger_Click;
                    BottomCommandBar.SecondaryCommands.Add(WinExButton);

                    AppBarButton TerminalButton = new AppBarButton
                    {
                        Icon = new FontIcon { Glyph = "\uE756" },
                        Label = Globalization.GetString("Operate_Text_OpenInTerminal")
                    };
                    TerminalButton.Click += OpenInTerminal_Click;
                    BottomCommandBar.SecondaryCommands.Add(TerminalButton);

                    BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                    AppBarButton PropertyButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Tag),
                        Label = Globalization.GetString("Operate_Text_Property")
                    };
                    PropertyButton.Click += ParentProperty_Click;
                    BottomCommandBar.SecondaryCommands.Add(PropertyButton);
                }
            }
        }

        private void ListHeader_DoubleTapped(object sender, Windows.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void LnkOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is HyperlinkStorageItem Item)
            {
                if (!SettingControl.IsDetachTreeViewAndPresenter && !SettingControl.IsDisplayHiddenItem)
                {
                    if (string.IsNullOrWhiteSpace(Item.TargetPath))
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                        return;
                    }

                    PathAnalysis Analysis = new PathAnalysis(Item.TargetPath, string.Empty);
                    while (Analysis.HasNextLevel)
                    {
                        if (WIN_Native_API.CheckIfHidden(Analysis.NextFullPath()))
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_NeedOpenHiddenSwitch_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await Dialog.ShowAsync().ConfigureAwait(false);
                            return;
                        }
                    }
                }

                StorageFolder ParentFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Item.TargetPath));

                await Container.OpenTargetFolder(ParentFolder).ConfigureAwait(true);

                if (FileCollection.FirstOrDefault((SItem) => SItem.Path == Item.TargetPath) is FileSystemStorageItemBase Target)
                {
                    ItemPresenter.ScrollIntoView(Target);
                    SelectedItem = Target;
                }
            }
        }

        private async void ViewControlRefreshContainer_RefreshRequested(Microsoft.UI.Xaml.Controls.RefreshContainer sender, Microsoft.UI.Xaml.Controls.RefreshRequestedEventArgs args)
        {
            Windows.Foundation.Deferral Deferral = args.GetDeferral();

            try
            {
                if (WIN_Native_API.CheckExist(Container.CurrentFolder.Path))
                {
                    await Container.DisplayItemsInFolder(Container.CurrentFolder, true).ConfigureAwait(true);
                }
                else
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_RefreshButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, "Refresh ItemPresenter failed");
            }
            finally
            {
                await Task.Delay(700).ConfigureAwait(true);

                Deferral.Complete();
            }
        }

        private void MulSelect_Click(object sender, RoutedEventArgs e)
        {
            EmptyFlyout.Hide();

            if (ItemPresenter.SelectionMode == ListViewSelectionMode.Extended)
            {
                ItemPresenter.SelectionMode = ListViewSelectionMode.Multiple;
            }
            else
            {
                ItemPresenter.SelectionMode = ListViewSelectionMode.Extended;
            }
        }
    }
}

