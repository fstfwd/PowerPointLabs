﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ImageProcessor;
using MahApps.Metro.Controls.Dialogs;
using Microsoft.Office.Core;
using PowerPointLabs.AutoUpdate;
using PowerPointLabs.ImageSearch.Model;
using PowerPointLabs.ImageSearch.Presentation;
using PowerPointLabs.ImageSearch.SearchEngine;
using PowerPointLabs.ImageSearch.SearchEngine.Options;
using PowerPointLabs.ImageSearch.SearchEngine.VO;
using PowerPointLabs.ImageSearch.Util;
using PowerPointLabs.Models;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;

namespace PowerPointLabs.ImageSearch
{
    /// <summary>
    /// Interaction logic for ImageSearchPane.xaml
    /// </summary>
    public partial class ImageSearchPane
    {
        // list that holds search result item
        public ObservableCollection<ImageItem> SearchList { get; set; }

        // list that holds preview item
        public ObservableCollection<ImageItem> PreviewList { get; set; }

        // a timer used to download full-size image at background
        public Timer PreviewTimer { get; set; }

        private const int TimerInterval = 2000;

        // a background presentation that will do the preview processing
        public StylesPreviewPresentation PreviewPresentation { get; set; }

        public GoogleEngine SearchEngine { get; set; }

        public bool IsOpen { get; set; }

        #region Initialization
        public ImageSearchPane()
        {
            InitializeComponent();

            // TODO ENHANCEMENT show some instructions when lists are empty
            SearchList = new ObservableCollection<ImageItem>();
            PreviewList = new ObservableCollection<ImageItem>();
            SearchListBox.DataContext = this;
            PreviewListBox.DataContext = this;
            IsOpen = true;

            var isTempFolderReady = TempPath.InitTempFolder();
            if (isTempFolderReady)
            {
                InitSearchEngine();
                InitPreviewPresentation();
                InitPreviewTimer();
            }
        }

        private void InitSearchEngine()
        {
            // TODO MUST load options from config
            SearchEngine = new GoogleEngine(new GoogleOptions())
                .WhenSucceed(WhenSearchSucceed())
                .WhenCompleted(WhenSearchCompleted())
                .WhenFail(response => {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        this.ShowMessageAsync("Error",
                            "Failed to search images. Please check your network, or the daily API quota is ran out.");
                    }));
                });
        }

        private GoogleEngine.WhenCompletedEventDelegate WhenSearchCompleted()
        {
            return isSuccess =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SearchProgressRing.IsActive = false;
                    var isThereMoreSearchResults = !RemoveElementInSearchList(item => item.IsToDelete);
                    if (isSuccess 
                        && isThereMoreSearchResults 
                        && SearchList.Count + GoogleEngine.NumOfItemsPerRequest - 1/*loadMore item*/ 
                        <= GoogleEngine.MaxNumOfItems)
                    {
                        SearchList.Add(new ImageItem
                        {
                            ImageFile = TempPath.LoadMoreImgPath
                        });
                    }
                }));
            };
        }

        // TODO util
        private bool RemoveElementInSearchList(Func<ImageItem, bool> cond)
        {
            var isAnyElementRemoved = false;
            for (var i = 0; i < SearchList.Count;)
            {
                if (cond(SearchList[i]))
                {
                    SearchList.RemoveAt(i);
                    isAnyElementRemoved = true;
                }
                else
                {
                    i++;
                }
            }
            return isAnyElementRemoved;
        }

        private GoogleEngine.WhenSucceedEventDelegate WhenSearchSucceed()
        {
            return (searchResults, startIdx) =>
            {
                searchResults.Items = searchResults.Items ?? new List<SearchResult>();
                for (var i = 0; i < GoogleEngine.NumOfItemsPerRequest; i++)
                {
                    var item = SearchList[startIdx + i];
                    if (i >= searchResults.Items.Count)
                    {
                        item.IsToDelete = true;
                        continue;
                    }

                    var searchResult = searchResults.Items[i];
                    var thumbnailPath = TempPath.GetPath("thumbnail");

                    new Downloader()
                        .Get(searchResult.Image.ThumbnailLink, thumbnailPath)
                        .After(() =>
                        {
                            item.ImageFile = thumbnailPath;
                            item.FullSizeImageUri = searchResult.Link;
                        })
                        .Start();
                }
            };
        }

        private void InitPreviewPresentation()
        {
            PreviewPresentation = new StylesPreviewPresentation();
            PreviewPresentation.Open(withWindow: false, focus: false);
        }

        // intent:
        // when select a thumbnail for some time (defined by TimerInterval),
        // try to download its full size version for better preview and can be used for insertion
        private void InitPreviewTimer()
        {
            PreviewTimer = new Timer { Interval = TimerInterval };
            PreviewTimer.Elapsed += (sender, args) =>
            {
                // in timer thread
                PreviewTimer.Stop();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // UI thread starts
                    var imageItem = SearchListBox.SelectedValue as ImageItem;
                    // if already have cached full-size image, ignore
                    if (imageItem == null || imageItem.FullSizeImageFile != null)
                    {
                        return;
                    }

                    // preview progress ring will be off, after preview processing is done
                    PreviewProgressRing.IsActive = true;

                    var fullsizeImageFile = TempPath.GetPath("fullsize");
                    new Downloader()
                        .Get(imageItem.FullSizeImageUri, fullsizeImageFile)
                        .After(AfterDownloadFullSizeImage(imageItem, fullsizeImageFile))
                        .OnError(WhenFailDownloadFullSizeImage())
                        .Start();
                }));
            };
        }

        private Downloader.ErrorEventDelegate WhenFailDownloadFullSizeImage()
        {
            return () =>
            {
                // in downloader thread
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    PreviewProgressRing.IsActive = false;
                }));
            };
        }

        private Downloader.AfterDownloadEventDelegate AfterDownloadFullSizeImage(ImageItem imageItem, string fullsizeImageFile)
        {
            return () =>
            {
                // in downloader thread
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // UI thread again
                    // store back to image, so cache it
                    imageItem.FullSizeImageFile = fullsizeImageFile;

                    // intent: during download, selected item may have been changed to another one
                    var currentImageItem = SearchListBox.SelectedValue as ImageItem;
                    if (currentImageItem == null)
                    {
                        PreviewProgressRing.IsActive = false;
                    }
                    else if (currentImageItem.ImageFile == imageItem.ImageFile)
                    {
                        // preview progress ring will be off, after preview
                        DoPreview(imageItem);
                    }
                }));
            };
        }
        # endregion

        private void SearchButton_OnClick(object sender, RoutedEventArgs e)
        {
            var query = SearchTextBox.Text;
            if (query.Trim().Length == 0)
            {
                return;
            }

            PrepareToSearch(GoogleEngine.NumOfItemsPerSearch);
            SearchEngine.Search(query);
        }

        private void PrepareToSearch(int expectedNumOfImages, bool isListClearNeeded = true)
        {
            // clear search list, and show a list of
            // 'Loading...' images
            if (isListClearNeeded)
            {
                SearchList.Clear();
            }
            for (var i = 0; i < expectedNumOfImages; i++)
            {
                SearchList.Add(new ImageItem
                {
                    ImageFile = TempPath.LoadingImgPath
                });
            }
            SearchProgressRing.IsActive = true;
        }

        // intent:
        // press Enter in the textbox to start searching
        private void SearchTextBox_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SearchButton_OnClick(sender, e);
                SearchTextBox.SelectAll();
            }
        }

        // intent:
        // do previewing, when search result item is (not) selected
        private void SearchListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var image = (ImageItem) SearchListBox.SelectedValue;
            if (image == null || image.ImageFile == TempPath.LoadingImgPath)
            {
                PreviewList.Clear();
                PreviewProgressRing.IsActive = false;
            } 
            else if (image.ImageFile == TempPath.LoadMoreImgPath)
            {
                image.ImageFile = TempPath.LoadingImgPath;
                PrepareToSearch(GoogleEngine.NumOfItemsPerRequest - 1, isListClearNeeded: false);
                SearchEngine.SearchMore();
            }
            else
            {
                PreviewTimer.Stop();

                DoPreview(image);

                // when timer ticks, try to download full size image to replace
                PreviewTimer.Start();
            }
        }

        // do preview processing
        private void DoPreview(ImageItem imageItem)
        {
            // ui thread
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var selectedId = PreviewListBox.SelectedIndex;
                PreviewProgressRing.IsActive = true;
                PreviewList.Clear();
                PreviewPresentation.PreviewStyles(imageItem);

                PreviewList.Add(new ImageItem
                {
                    ImageFile = PreviewPresentation.DirectTextStyleImagePath
                });
                PreviewList.Add(new ImageItem
                {
                    ImageFile = PreviewPresentation.BlurStyleImagePath
                });
                PreviewList.Add(new ImageItem
                {
                    ImageFile = PreviewPresentation.TextboxStyleImagePath
                });
                PreviewListBox.SelectedIndex = selectedId;

                // try catch finally?
                PreviewProgressRing.IsActive = false;
            }));
        }


        // intent:
        // allow arrow keys to navigate the search result items in the list
        private void ListBox_OnKeyDown(object sender, KeyEventArgs e)
        {
            var listbox = sender as ListBox;
            if (listbox == null || listbox.Items.Count <= 0)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Right:
                case Key.Down:
                    if (!listbox.Items.MoveCurrentToNext())
                    {
                        listbox.Items.MoveCurrentToLast();
                    }
                    break;

                case Key.Left:
                case Key.Up:
                    if (!listbox.Items.MoveCurrentToPrevious())
                    {
                        listbox.Items.MoveCurrentToFirst();
                    }
                    break;

                default:
                    return;
            }

            e.Handled = true;
            var item = (ListBoxItem) listbox.ItemContainerGenerator.ContainerFromItem(listbox.SelectedItem);
            item.Focus();
        }

        // intent: focus on search textbox when
        // pane is open
        public void FocusSearchTextBox()
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
        }

        // intent: drag splitter to change grid width
        private void Splitter_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            ImagesLabGrid.ColumnDefinitions[0].Width = new GridLength(ImagesLabGrid.ColumnDefinitions[0].ActualWidth + e.HorizontalChange);
        }

        // enable & disable insert button
        private void PreivewListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PreviewListBox.SelectedValue != null)
                {
                    PreviewInsert.IsEnabled = true;
                }
                else
                {
                    PreviewInsert.IsEnabled = false;
                }
            }));
        }

        // rmb to close background presentation
        private void ImageSearchPane_OnClosing(object sender, CancelEventArgs e)
        {
            IsOpen = false;
            if (PreviewPresentation != null)
            {
                PreviewPresentation.Close();
            }
        }

        // TODO DRY
        private void PreviewInsert_OnClick(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PreviewTimer.Stop();
                PreviewProgressRing.IsActive = true;
            
                // TODO know other style to apply
                // selected value can be null, this works if there's cache for full size image
                if (((ImageItem) SearchListBox.SelectedValue).FullSizeImageFile != null)
                {
                    var thisSlide = PowerPointCurrentPresentationInfo.CurrentSlide;
                    foreach (PowerPoint.Shape shape in thisSlide.Shapes)
                    {
                        if (shape.Name.StartsWith("pptImagesLab"))
                        {
                            shape.Delete();
                        }
                    }
                    var imageShape = thisSlide.Shapes.AddPicture(((ImageItem) PreviewListBox.SelectedValue).FullSizeImageFile, MsoTriState.msoFalse,
                        MsoTriState.msoTrue, 0, 0);
                    imageShape.Name = "pptImagesLab" + DateTime.Now.GetHashCode();
                    FitToSlide.AutoFit(imageShape, PreviewPresentation);
                    imageShape.ZOrder(MsoZOrderCmd.msoSendToBack);
                    PreviewProgressRing.IsActive = false;
                }
                else
                {
                    // download full-size image & apply style's algorithm
                    var imageItem = (ImageItem) SearchListBox.SelectedValue;
                    var fullsizeImageFile = TempPath.GetPath("fullsize");

                    new Downloader()
                        .Get(imageItem.FullSizeImageUri, fullsizeImageFile)
                        .After(() =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                imageItem.FullSizeImageFile = fullsizeImageFile;
                                if (SearchListBox.SelectedValue != null
                                    && (SearchListBox.SelectedValue as ImageItem).ImageFile == imageItem.ImageFile)
                                {
                                    DoPreview(imageItem);
                                }
                                var thisSlide = PowerPointCurrentPresentationInfo.CurrentSlide;
                                foreach (PowerPoint.Shape shape in thisSlide.Shapes)
                                {
                                    if (shape.Name.StartsWith("pptImagesLab"))
                                    {
                                        shape.Delete();
                                    }
                                }
                                var imageShape = thisSlide.Shapes.AddPicture(fullsizeImageFile, MsoTriState.msoFalse,
                                    MsoTriState.msoTrue, 0, 0);
                                imageShape.Name = "pptImagesLab" + DateTime.Now.GetHashCode();
                                FitToSlide.AutoFit(imageShape, PreviewPresentation);
                                imageShape.ZOrder(MsoZOrderCmd.msoSendToBack);
                                PreviewProgressRing.IsActive = false;
                            }));
                        })
                        .Start();
                }
            }));
        }

        private void PreviewDisplayToggleSwitch_OnIsCheckedChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PreviewDisplayToggleSwitch.IsChecked == true)
                {
                    ImagesLabGrid.ColumnDefinitions[0].Width = new GridLength(620);
                } 
                else 
                {
                    ImagesLabGrid.ColumnDefinitions[0].Width = new GridLength(320);
                }
            }));
        }

        private void ImageSearchPane_OnIsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var isFocused = (bool) e.NewValue;
            if (!isFocused) return;
            
            var image = (ImageItem) SearchListBox.SelectedValue;
            if (image == null || image.ImageFile == TempPath.LoadingImgPath)
            {
                PreviewList.Clear();
                PreviewProgressRing.IsActive = false;
            }
            else
            {
                DoPreview(image);
            }
        }
    }
}
