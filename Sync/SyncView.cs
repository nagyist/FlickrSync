using System;
using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.IO;
using FlickrNet;
using System.Windows.Media.Imaging;
using System.Threading;
using ThumbDBLib;
using System.Diagnostics;
using System.Collections.Generic;

namespace FlickrSync
{
    public partial class SyncView : Form
    {
        const int ThumbnailSize = 75;

        struct ThumbnailTask
        {
            public string org;
            public string key;
            public bool local;
            public SyncItem.Actions action;
        };

        ArrayList SyncFolders;
        ArrayList SyncItems;
        static DateTime SyncDate;

        bool SyncStarted = false;
        bool SyncAborted = false;
        bool Finished = false;
        Thread SyncThread = null;
        Thread ThumbnailThread = null;

        ArrayList Tasks;
        ArrayList NewSets;

        private delegate void ChangeProgressBarCallBack(ProgressBars pb, ProgressValues type, int value, string status);
        private delegate void MarkImagesCallBack(int index, bool success);
        private delegate void RefreshImagesCallBack(int index);
        private delegate void FinishCallBack();

        enum ProgressBars { PBSync = 0, PBPhoto };
        enum ProgressValues { PBValue = 0, PBMinimum, PBMaximum };

        public SyncView(ArrayList pSyncFolders)
        {
            InitializeComponent();
            SyncItems=new ArrayList();
            Tasks = new ArrayList();
            NewSets = new ArrayList();
            SyncFolders = pSyncFolders;

            CalcSync();
        }

        private void UpdateThumbnails()

        {
            try
            {
                string thumb_path = "";
                ThumbDB th = null;

                foreach (ThumbnailTask tt in Tasks)
                {
                    int index = imageList1.Images.IndexOfKey(tt.key);
                    if (index < 0) continue;

                    if (tt.local)
                    {
                        Bitmap bm = null;
                        int bmSize = ThumbnailSize;

                        try
                        {
                            // NOTE: Specific Windows Code - Get thumbnail from Thumbs.db for faster update
                            string filepath = Path.GetDirectoryName(tt.org);
                            string thumb_path_new = filepath + Path.DirectorySeparatorChar + "Thumbs.db";
                            if (thumb_path_new != thumb_path)
                            {
                                th = new ThumbDB(thumb_path_new);
                                thumb_path = thumb_path_new;
                            }

                            if (th != null)
                                bm = (Bitmap)th.GetThumbnailImage(Path.GetFileName(tt.org));
                        }
                        catch (Exception)
                        {
                        }

                        if (bm == null)
                        {
                            try
                            {
                                bm = new Bitmap(tt.org);
                                //bmSize = ThumbnailSize*2; 

                                if (bm == null)
                                    continue;
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                        }

                        Rectangle rc = new Rectangle(0, 0, bmSize, bmSize);

                        Bitmap bm2;
                        if (bm.Width > bm.Height)
                        {
                            bm2 = new Bitmap(bm, bmSize * bm.Width / bm.Height, bmSize);
                            rc.X = (bm2.Width - bmSize) / 2;
                        }
                        else
                        {
                            bm2 = new Bitmap(bm, bmSize, bmSize * bm.Height / bm.Width);
                            rc.Y = (bm2.Height - bmSize) / 2;
                        }

                        bm2 = bm2.Clone(rc, bm2.PixelFormat);
                        imageList1.Images[index] = bm2;

                        bm.Dispose();
                        bm2.Dispose();
                    }
                    else
                    {
                        try
                        {
                            imageList1.Images[index] = Bitmap.FromStream(FlickrSync.ri.PhotoThumbnail(tt.org));
                        }
                        catch (Exception)
                        {
                        }
                    }

                    if (tt.action == SyncItem.Actions.ActionNone)
                    {
                        Image img = (Image) imageList1.Images[index].Clone();
                        Graphics g = Graphics.FromImage(img);
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                        g.FillRectangle(new SolidBrush(Color.FromArgb(192, Color.White)), new Rectangle(0, 0, ThumbnailSize, ThumbnailSize));
                        imageList1.Images[index] = img;
                    }

                    this.Invoke(new RefreshImagesCallBack(RefreshImages), new object[] { index });
                }
            }
            catch(Exception)
            {
            }
        }

        private Image Thumbnail(string org,string key,bool local,SyncItem.Actions action)
        {
            ThumbnailTask tt;
            tt.org = org;
            tt.key = key;
            tt.local = local;
            tt.action = action;
            Tasks.Add(tt);

            switch (tt.action)
            {
                case SyncItem.Actions.ActionUpload:
                    return Properties.Resources.icon_new;
                case SyncItem.Actions.ActionReplace:
                    return Properties.Resources.icon_replace;
                case SyncItem.Actions.ActionDelete:
                    return Properties.Resources.icon_delete;
                case SyncItem.Actions.ActionNone:
                    return Properties.Resources.icon_none;
            }

            return Properties.Resources.flickrsync;
        }

        private void RefreshImages(int index)
        {
            try
            {
                listViewToSync.Invalidate(listViewToSync.Items[index].Bounds);
            }
            catch (Exception)
            {
            }
        }

        private List<FileInfo> GetFiles(SyncFolder sf)
        {
            var files = new List<FileInfo>();

            try
            {
                DirectoryInfo dir = new DirectoryInfo(sf.FolderPath);
                if (!dir.Exists)
                {
                    if (FlickrSync.messages_level != FlickrSync.MessagesLevel.MessagesNone)
                    {
                        if (MessageBox.Show("Folder " + sf.FolderPath + " no longer exists. Remove from configuration?", "Warning", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        {
                            FlickrSync.li.Remove(sf.FolderPath);
                            FlickrSync.Log(FlickrSync.LogLevel.LogAll, sf.FolderPath + " deleted from configuration");
                        }
                        else
                            FlickrSync.Log(FlickrSync.LogLevel.LogAll, sf.FolderPath + " does not exist");
                    }

                    return files;
                }

                int count = Properties.Settings.Default.Extensions.Count;

                foreach (string ext in Properties.Settings.Default.Extensions)
                {
                    FileInfo[] foundFiles = dir.GetFiles("*." + ext);

                    foreach (FileInfo file in foundFiles)
                    {
                        // Some apps generate hidden files that are not meant to be uploaded but has the
                        // same extension as the image (e.g. "._name.jpg" with size 4,096 bytes).
                        if ((file.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                        {
                            continue;
                        }

                        // Make sure the file is valid, otherwise, is will cause failure later when attempting
                        // to upload to Flickr.
                        if (file.Length == 0)
                        {
                            continue;
                        }

                        files.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                this.Cursor = Cursors.Default;
                FlickrSync.Error("Error accessing path: " + sf.FolderPath, ex, FlickrSync.ErrorType.Normal);
                this.Close();
            }

            files.Sort(new SortLastWriteHelper());

            return files;
        }

        private bool ShouldExcludePhoto(SyncFolder sf, FileInfo fi, out ImageInfo ii)
        {
            ii = new ImageInfo();
            string[] ftags = sf.FilterTags.Split(';');
            for (int i = 0; i < ftags.GetLength(0); i++)
            {
                ftags[i] = ftags[i].Trim();
            }

            bool include = true;

            if (sf.FilterType == SyncFolder.FilterTypes.FilterIncludeTags ||
                sf.FilterType == SyncFolder.FilterTypes.FilterStarRating ||
                sf.SyncMethod == SyncFolder.Methods.SyncDateTaken ||
                sf.SyncMethod == SyncFolder.Methods.SyncTitleOrFilename)
            {
                ii.Load(fi.FullName, ImageInfo.FileTypes.FileTypeUnknown);
            }

            if (sf.FilterType == SyncFolder.FilterTypes.FilterIncludeTags)
            {
                include = false;

                foreach (string tag in ii.GetTagsArray())
                {
                    foreach (string tag2 in ftags)
                    {
                        if (string.Equals(tag, tag2, StringComparison.OrdinalIgnoreCase))
                        {
                            include = true;
                            break;
                        }
                    }

                    if (include)
                    {
                        break;
                    }
                }
            }

            if (sf.FilterType == SyncFolder.FilterTypes.FilterStarRating)
            {
                if (sf.FilterStarRating > ii.GetStarRating())
                {
                    include = false;
                }
            }

            /*if (fi.Length > max_size)
                include = false;*/

            return !include;
        }

        private string GetImageNameFromFileName(string filename)
        {
            string name = filename;

            foreach (string ext in Properties.Settings.Default.Extensions)
            {
                if (filename.EndsWith("." + ext, StringComparison.CurrentCultureIgnoreCase))
                {
                    name = name.Remove(name.Length - 4);
                }
            }

            if (name.EndsWith(" "))  // flickr removes ending space - need to replace with something else
            {
                name = name.Remove(name.Length - 1).Insert(name.Length - 1, @"|");
            }

            return name;
        }

        private SyncItem CreateSyncItem(SyncFolder sf, FileInfo fi, ImageInfo ii, SyncItem.Actions action, string name, long max_size)
        {
            var si = new SyncItem();

            si.Action = action;

            if (fi.Length > max_size)
            {
                si.Action = SyncItem.Actions.ActionNone;
            }

            si.Filename = fi.FullName;
            si.SetId = sf.SetId;
            si.SetTitle = sf.SetTitle;
            si.SetDescription = sf.SetDescription;
            si.NoDeleteTags = sf.NoDeleteTags;

            if (!string.IsNullOrEmpty(ii.GetTitle()) && sf.SyncMethod != SyncFolder.Methods.SyncFilename)
            {
                si.Title = ii.GetTitle();
            }
            else
            {
                si.Title = name;
            }

            si.Description = ii.GetDescription();
            si.Tags = ii.GetTagsArray();

            if (!string.IsNullOrEmpty(ii.GetCity()))
            {
                si.Tags.Add(ii.GetCity());
            }

            if (!string.IsNullOrEmpty(ii.GetCountry()))
            {
                si.Tags.Add(ii.GetCountry());
            }

            si.GeoLat = ii.GetGeo(lat: true);
            si.GeoLong = ii.GetGeo(lat: false);

            si.FolderPath = sf.FolderPath;
            si.Permission = sf.Permission;

            return si;
        }

        private bool IsPhotoMatch(SyncFolder sf, PhotoInfo pi, ImageInfo ii, string name)
        {
            if (sf.SyncMethod == SyncFolder.Methods.SyncFilename && pi.Title == name)
            {
                return true;
            }

            if (sf.SyncMethod == SyncFolder.Methods.SyncDateTaken && pi.DateTaken == ii.GetDateTaken())
            {
                return true;
            }

            if (sf.SyncMethod == SyncFolder.Methods.SyncTitleOrFilename)
            {
                string title = ii.GetTitle();
                if (string.IsNullOrEmpty(title))
                {
                    title = name;
                }

                if (pi.Title == title)
                {
                    return true;
                }
            }

            return false;
        }

        private List<PhotoInfo> GetPhotos(SyncFolder sf)
        {
            var photosInSet = new List<PhotoInfo>();

            if (string.IsNullOrEmpty(sf.SetId))
            {
                return photosInSet;
            }

            int retryCount = 0;
            bool success = false;

            while (!success)
            {
                try
                {
                    photosInSet.Clear();

                    foreach (Photo p in FlickrSync.ri.SetPhotos(sf.SetId))
                    {
                        // workaround since media type and p.MachineTags is not working on FlickrNet 2.1.5
                        if (p.CleanTags != null)
                        {
                            if (p.CleanTags.IndexOf("flickrsync:type=video", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.CleanTags.IndexOf("flickrsync:cmd=skip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.CleanTags.IndexOf("flickrsync:type:video", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                p.CleanTags.IndexOf("flickrsync:cmd:skip", StringComparison.OrdinalIgnoreCase) >= 0)
                                continue;
                        }

                        PhotoInfo pi = new PhotoInfo();
                        pi.Title = p.Title;
                        pi.DateTaken = p.DateTaken;
                        pi.DateSync = sf.LastSync;
                        pi.DateUploaded = p.DateUploaded;
                        pi.PhotoId = p.PhotoId;
                        pi.Found = false;
                        photosInSet.Add(pi);
                    }

                    success = true;
                }
                catch (Exception)
                {
                    if (retryCount++ <= 5)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            return photosInSet;
        }

        private void CalcSync()
        {
            this.Cursor = Cursors.WaitCursor;
            this.Refresh();
            Flickr.CacheDisabled = true;
            SyncDate = DateTime.Now;
            int count_images = 0;
            long max_size = FlickrSync.ri.MaxFileSize();

            if (Properties.Settings.Default.UseThumbnailImages)
            {
                imageList1.Images.Clear();
                imageList1.ImageSize = new System.Drawing.Size(75, 75);
            }

            foreach (SyncFolder sf in SyncFolders)
            {
                Application.DoEvents();

                if (string.IsNullOrEmpty(sf.SetId) && string.IsNullOrEmpty(sf.SetTitle))
                {
                    this.Cursor = Cursors.Default;
                    if (FlickrSync.messages_level == FlickrSync.MessagesLevel.MessagesAll)
                    {
                        MessageBox.Show(sf.FolderPath + " has no associated Set", "Warning");
                    }

                    FlickrSync.Log(FlickrSync.LogLevel.LogAll, sf.FolderPath + " has no associated set");
                    this.Cursor = Cursors.WaitCursor;
                    continue;
                }

                List<FileInfo> files = GetFiles(sf);

                // Skip sync folders that have same number of photos as photo sets
                if (!string.IsNullOrEmpty(sf.SetId))
                {
                    Photoset photoset = FlickrSync.ri.GetPhotoset(sf.SetId);
                    if (photoset.NumberOfPhotos == files.Count)
                    {
                        continue;
                    }
                }

                var photos = new List<PhotoInfo>();
                try
                {
                    photos = GetPhotos(sf);
                }
                catch (Exception ex)
                {
                    FlickrSync.Error("Error loading information from Set " + sf.SetId, ex, FlickrSync.ErrorType.Normal);
                    Close();
                }

                ListViewGroup group;
                if (string.IsNullOrEmpty(sf.SetId))
                {
                    group = new ListViewGroup("Folder: " + sf.FolderPath + "; Set: " + sf.SetTitle);
                }
                else
                {
                    try
                    {
                        group = new ListViewGroup("Folder: " + sf.FolderPath + "; Set: " + FlickrSync.ri.GetPhotoset(sf.SetId).Title);
                    }
                    catch (Exception)
                    {
                        group = new ListViewGroup("Folder: " + sf.FolderPath + "; Set: " + sf.SetId);
                    }
                }

                listViewToSync.Groups.Add(group);

                foreach (FileInfo fi in files)
                {
                    ImageInfo ii;
                    if (ShouldExcludePhoto(sf, fi, out ii))
                    {
                        continue;
                    }

                    string name = GetImageNameFromFileName(fi.Name);

                    // Matching photos
                    int pos = -1;
                    bool found = false;
                    foreach (PhotoInfo pi in photos)
                    {
                        pos++;

                        if (IsPhotoMatch(sf, pi, ii, name))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        if (ii.GetFileName() != fi.FullName)
                        {
                            ii.Load(fi.FullName, ImageInfo.FileTypes.FileTypeUnknown);
                        }

                        SyncItem si = CreateSyncItem(sf, fi, ii, SyncItem.Actions.ActionUpload, name, max_size);

                        int position = 0;
                        if (Properties.Settings.Default.UseThumbnailImages)
                        {
                            imageList1.Images.Add(count_images.ToString(), Thumbnail(fi.FullName, count_images.ToString(), true, si.Action));
                            position = count_images;
                            count_images++;
                        }

                        ListViewItem lvi;
                        if (si.Action != SyncItem.Actions.ActionNone)
                        {
                            lvi = listViewToSync.Items.Add("NEW: " + fi.Name, position);
                            lvi.ForeColor = Color.Blue;
                        }
                        else
                        {
                            lvi = listViewToSync.Items.Add("SKIP: " + fi.Name, position);
                            lvi.ForeColor = Color.LightGray;
                        }

                        lvi.ToolTipText = lvi.Text + " " + group.Header;
                        group.Items.Add(lvi);

                        si.item_id = lvi.Index;
                        SyncItems.Add(si);
                    }
                    else
                    {
                        photos[pos].Found = true;

                        // Compare time is based on local info DateSync because flickr clock could be misaligned with local clock
                        DateTime compare = photos[pos].DateSync;
                        if (compare == new DateTime(2000, 1, 1))
                        {
                            compare = photos[pos].DateUploaded;
                        }

                        if (compare < fi.LastWriteTime)
                        {
                            if (ii.GetFileName() != fi.FullName)
                            {
                                ii.Load(fi.FullName, ImageInfo.FileTypes.FileTypeUnknown);
                            }

                            SyncItem si = CreateSyncItem(sf, fi, ii, SyncItem.Actions.ActionReplace, name, max_size);

                            if (sf.LastSync == (new DateTime(2000, 1, 1)) && sf.NoInitialReplace)
                            {
                                si.Action = SyncItem.Actions.ActionNone;
                            }

                            si.PhotoId = photos[pos].PhotoId;

                            int position = 0;
                            if (Properties.Settings.Default.UseThumbnailImages)
                            {
                                imageList1.Images.Add(count_images.ToString(), Thumbnail(fi.FullName, count_images.ToString(), true, SyncItem.Actions.ActionReplace));
                                position = count_images;
                                count_images++;
                            }

                            ListViewItem lvi;
                            if (si.Action != SyncItem.Actions.ActionNone)
                            {
                                lvi = listViewToSync.Items.Add("REPLACE: " + fi.Name, position);
                                lvi.ForeColor = Color.Black;
                            }
                            else
                            {
                                lvi = listViewToSync.Items.Add("SKIP: " + fi.Name, position);
                                lvi.ForeColor = Color.LightGray;
                            }

                            lvi.ToolTipText = lvi.Text+" "+group.Header;
                            group.Items.Add(lvi);

                            si.item_id = lvi.Index;
                            SyncItems.Add(si);
                        }
                    }
                }

                if (!sf.NoDelete)
                {
                    foreach (PhotoInfo pi in photos)
                    {
                        if (!pi.Found)
                        {
                            SyncItem si = new SyncItem();
                            si.Action = SyncItem.Actions.ActionDelete;
                            si.PhotoId = pi.PhotoId;
                            si.Title = pi.Title;
                            si.SetId = sf.SetId;

                            int position = 0;
                            if (Properties.Settings.Default.UseThumbnailImages)
                            {
                                imageList1.Images.Add(count_images.ToString(), Thumbnail(pi.PhotoId, count_images.ToString(), false, SyncItem.Actions.ActionDelete));
                                position = count_images;
                                count_images++;
                            }

                            ListViewItem lvi = listViewToSync.Items.Add("DELETE: " + pi.Title,position);
                            lvi.ForeColor = Color.Red;
                            lvi.ToolTipText = lvi.Text + " " + group.Header;
                            group.Items.Add(lvi);

                            si.item_id = lvi.Index;
                            SyncItems.Add(si);
                        }
                    }
                }
            }

            FlickrSync.Log(FlickrSync.LogLevel.LogAll, "Prepared Synchronization successful");

            Flickr.CacheDisabled = false;
            this.Cursor = Cursors.Default;
        }

        private void SyncView_Load(object sender, EventArgs e)
        {
            if (SyncItems.Count == 0)
            {
                if (FlickrSync.messages_level != FlickrSync.MessagesLevel.MessagesNone) 
                    MessageBox.Show("Nothing to do", "FlickrSync Information");

                FlickrSync.Log(FlickrSync.LogLevel.LogBasic, "Nothing to do");

                this.Close();
                return;
            }

            Program.MainFlickrSync.Visible = false;
            WindowState = FormWindowState.Maximized;          

            ThreadStart ts = new ThreadStart(UpdateThumbnails);
            ThumbnailThread = new Thread(ts);
            ThumbnailThread.IsBackground = true;
            ThumbnailThread.Start();
        }

        private void ChangeProgressBar(ProgressBars pb, ProgressValues type, int value)
        {
            ChangeProgressBar(pb, type, value, null);
        }

        private void ChangeProgressBar(ProgressBars pb, ProgressValues type, int value, string status)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new ChangeProgressBarCallBack(ChangeProgressBar), new object[] { pb, type, value, status });
                }
                else
                {
                    if ((status != null) && (status.Length > 0))
                    {
                        labelSync.Text = "Synchronizing: " + status;
                    }
                    ProgressBar p;
                    if (pb == ProgressBars.PBSync)
                        p = progressBarSync;
                    else
                        p = progressBarPhoto;

                    switch (type)
                    {
                        case ProgressValues.PBValue:
                            if (value < p.Minimum)
                                value = p.Minimum;
                            if (value > p.Maximum)
                                value = p.Maximum;

                            p.Value = value;
                            break;
                        case ProgressValues.PBMinimum:
                            p.Minimum = value;
                            break;
                        case ProgressValues.PBMaximum:
                            p.Maximum = value;
                            break;
                    }

                    if (pb == ProgressBars.PBSync)
                    {
                        int percent = 0;
                        if (p.Maximum != 0)
                            percent = p.Value * 100 / p.Maximum;

                        if (percent < 0)
                            percent = 0;
                        if (percent > 100)
                            percent = 100;

                        this.Text = "FlickrSync Synchronizing..." + percent.ToString() + @"%";
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private void FlickrProgress(int value)
        {
            ChangeProgressBar(ProgressBars.PBPhoto, ProgressValues.PBValue, value);
        }

        private void MarkCompleted(int index, bool success)
        {
            try
            {
                ListViewItem lvi = listViewToSync.Items[index];

                lvi.ForeColor = SystemColors.WindowText;
                lvi.BackColor = Color.FromArgb(220, 250, 220);

                if (Properties.Settings.Default.UseThumbnailImages)
                {
                    Image img = (Image)lvi.ImageList.Images[lvi.ImageIndex].Clone();
                    Graphics g = Graphics.FromImage(img);
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                    g.FillRectangle(new SolidBrush(Color.FromArgb(192, Color.White)), new Rectangle(0, 0, ThumbnailSize, ThumbnailSize));
                    g.DrawImage(success ? Properties.Resources.check : Properties.Resources.skip, ThumbnailSize - 25, ThumbnailSize - 25);

                    lvi.ImageList.Images[lvi.ImageIndex] = img;
                }

                listViewToSync.Invalidate(listViewToSync.Items[index].Bounds);
            }
            catch (Exception)
            {
            }
        }

        #region - Execute Sync -
        private SyncFolder GetSyncFolder(string SetId)
        {
            for (int i = 0; i < SyncFolders.Count; i++)
                if (((SyncFolder)SyncFolders[i]).SetId == SetId)
                    return (SyncFolder)SyncFolders[i];

            return null;
        }

        private FileInfo GetFileInfo(string filename)
        {
            FileInfo fi = null;

            try
            {
                fi = new FileInfo(filename);
            }
            catch (Exception)
            {
            }

            return fi;
        }

        private void Sort(SyncFolder sf)
        {
            if (sf.OrderType==SyncFolder.OrderTypes.OrderDefault)
                return;

            try
            {
                Photo[] photolist = FlickrSync.ri.SetPhotos(sf.SetId);
                ArrayList photo_array=new ArrayList();
                foreach (Photo p in photolist)
                    photo_array.Add(p);

                switch (sf.OrderType)
                {
                    case SyncFolder.OrderTypes.OrderDateTaken:
                        photo_array.Sort(new PhotoSortDateTaken());
                        break;
                    case SyncFolder.OrderTypes.OrderTitle:
                        photo_array.Sort(new PhotoSortTitle());
                        break;
                    case SyncFolder.OrderTypes.OrderTag:
                        photo_array.Sort(new PhotoSortTag());
                        break;
                }

                string[] ids = new string[photo_array.Count];
                for (int i = 0; i < photo_array.Count; i++)
                    ids[i] = ((Photo)photo_array[i]).PhotoId;

                FlickrSync.ri.SortSet(sf.SetId, ids);
            }
            catch (Exception ex)
            {
                FlickrSync.Error("Error sorting set "+sf.SetTitle+" ("+sf.SetId+")", ex, FlickrSync.ErrorType.Info);
            }
        }

        public void ExecuteSync()
        {
            try
            {
                ChangeProgressBar(ProgressBars.PBSync, ProgressValues.PBMinimum, 0);
                ChangeProgressBar(ProgressBars.PBSync, ProgressValues.PBMaximum, SyncItems.Count);
                int pos = 0;

                string CurrentSetId = "";
                bool ReplaceError = false;

                DateTime SyncProgressDate = new DateTime(2000, 1, 1);
                if (SyncFolders.Count>0)
                    SyncProgressDate=((SyncFolder)SyncFolders[0]).LastSync;

                int sequenceOfPhotosSkipped = 0;

                foreach (SyncItem si in SyncItems)
                {
                    if (SyncAborted)
                    {
                        Finish();
                        return;
                    }

                    string logmsg="";
                    switch(si.Action) {
                        case SyncItem.Actions.ActionDelete:
                            logmsg = "To Execute: Delete " + si.Title;
                            break;
                        case SyncItem.Actions.ActionNone:
                            logmsg="To Execute: Skip "+si.Filename;
                            break;
                        case SyncItem.Actions.ActionReplace:
                            logmsg="To Execute: Replace "+si.Filename;
                            break;
                        case SyncItem.Actions.ActionUpload:
                            logmsg="To Execute: New "+si.Filename;
                            break;
                    }
                    FlickrSync.Log(FlickrSync.LogLevel.LogAll, logmsg);

                    FileInfo fi = null;
                    if (si.Action != SyncItem.Actions.ActionDelete)
                        fi = GetFileInfo(si.Filename);

                    if (si.SetId != CurrentSetId)
                    {
                        if (ReplaceError)
                            ReplaceError = false;
                        else
                        {
                            // CurrentSetId synchronization is finished on previous set
                            SyncFolder sfCurrent = GetSyncFolder(CurrentSetId);
                            if (sfCurrent != null)
                            {
                                sfCurrent.LastSync = SyncDate;
                                Sort(sfCurrent);
                            }

                            SyncFolder sfNew = GetSyncFolder(si.SetId);
                            if (sfNew != null)
                                SyncProgressDate = sfNew.LastSync;
                        }

                        CurrentSetId = si.SetId;
                    }
                    else
                    {
                        // Since replaces are controlled from the date of last synchronization, when there is an error on replace, 
                        // sync should not continue. Otherwise the file in error would never get updated
                        if (ReplaceError)
                        {
                            continue;
                        }
                        else if (si.Action != SyncItem.Actions.ActionDelete)
                        {
                            SyncFolder sfCurrent = GetSyncFolder(CurrentSetId);

                            if (sfCurrent!=null && fi!=null && SyncProgressDate < fi.LastWriteTime) //Only updates LastSync to the last date if the new file is more recent than the last one
                                sfCurrent.LastSync = SyncProgressDate;
                        }
                    }

                    ChangeProgressBar(ProgressBars.PBPhoto, ProgressValues.PBMinimum, 0, si.SetTitle + " " + si.Filename);
                    if (fi != null)
                        ChangeProgressBar(ProgressBars.PBPhoto, ProgressValues.PBMaximum, (int)fi.Length);

                    FlickrSync.ri.SetProgress(new RemoteInfo.SetProgressType(FlickrProgress));

                    switch (si.Action)
                    {
                        case SyncItem.Actions.ActionUpload:
                            string PhotoId = "";

                            bool retry = true;
                            int retrycount=0;
                            bool skipPhoto = false;

                            //sometimes upload may fail, so retry a few times before giving up
                            while (retry)
                            {
                                try
                                {
                                    PhotoId = FlickrSync.ri.UploadPicture(si.Filename, si.Title, si.Description, si.Tags, si.Permission);
                                    retry = false;
                                    sequenceOfPhotosSkipped = 0;
                                }
                                catch (Exception ex)
                                {
                                    if (string.Equals(ex.Message, "Filetype was not recognised (5)", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(ex.Message, "General upload failure (3)", StringComparison.OrdinalIgnoreCase))
                                    {
                                        sequenceOfPhotosSkipped++;
                                        skipPhoto = true;
                                        break;
                                    }

                                    if (retrycount <= 5)
                                    {
                                        Thread.Sleep(TimeSpan.FromSeconds(1));
                                        retrycount++;
                                    }
                                    else
                                    {
                                        FlickrSync.Error("Error uploading picture to flickr: " + si.Filename, ex, FlickrSync.ErrorType.Normal);
                                        skipPhoto = true;
                                        sequenceOfPhotosSkipped++;
                                        break;
                                    }
                                }
                            }

                            // Abort if we had 50 bad photos in a row.
                            if (sequenceOfPhotosSkipped >= 50)
                            {
                                break;
                            }

                            // A photo can be corrupted, so let's move on to the next one.
                            if (skipPhoto)
                            {
                                FlickrSync.ri.ClearProgress();
                                pos++;
                                ChangeProgressBar(ProgressBars.PBSync, ProgressValues.PBValue, pos);
                                this.Invoke(new MarkImagesCallBack(MarkCompleted), new object[] { si.item_id, /* success */ false });
                                continue;
                            }

                            if (si.SetId != "")
                            {
                                retry = true; //associating the photo might fail if it's done right after uploading
                                retrycount = 0;

                                while (retry)
                                {
                                    try
                                    {
                                        FlickrSync.ri.PhotosetsAddPhoto(si.SetId, PhotoId);
                                        retry = false;
                                    }
                                    catch (FlickrApiException ex)
                                    {
                                        if (ex.Code == 3)    // Code 3 means Photo already in set - not really a problem
                                        {
                                            retry = false;
                                        }
                                        else
                                        {                       //only ask for user confirmation after retrying (retry several times if messages are disabled).
                                            if ((retrycount > 0 && FlickrSync.messages_level == FlickrSync.MessagesLevel.MessagesAll) ||
                                                (retrycount > 5 && FlickrSync.messages_level != FlickrSync.MessagesLevel.MessagesAll))
                                            {
                                                DialogResult resp = DialogResult.Abort;
                                                if (FlickrSync.messages_level == FlickrSync.MessagesLevel.MessagesAll)
                                                    resp=MessageBox.Show("Error associating photo to set: " + si.Filename + "\n" + ex.Message + "\nDo you want to continue?", "Error", MessageBoxButtons.AbortRetryIgnore);
                                                FlickrSync.Log(FlickrSync.LogLevel.LogBasic, "Error associating photo to set: " + si.Filename + "; " + ex.Message);

                                                if (resp == DialogResult.Abort)
                                                {
                                                    SyncAborted = true;
                                                    break;
                                                }

                                                if (resp == DialogResult.Ignore)
                                                    retry = false;
                                            }
                                            else
                                            {
                                                Thread.Sleep(TimeSpan.FromSeconds(1));
                                            }

                                            retrycount++;
                                        }
                                    }
                                    catch (Exception ex2) // all other exceptions do the same
                                    {                     //only ask for user confirmation after retrying (retry several times if messages are disabled).
                                        if ((retrycount > 0 && FlickrSync.messages_level == FlickrSync.MessagesLevel.MessagesAll) ||
                                            (retrycount > 5 && FlickrSync.messages_level != FlickrSync.MessagesLevel.MessagesAll))
                                        {
                                            DialogResult resp = DialogResult.Abort;
                                            if (FlickrSync.messages_level == FlickrSync.MessagesLevel.MessagesAll)
                                                resp=MessageBox.Show("Error associating photo to set: " + si.Filename + "\n" + ex2.Message + "\nDo you want to continue?", "Error", MessageBoxButtons.AbortRetryIgnore);
                                            FlickrSync.Log(FlickrSync.LogLevel.LogBasic, "Error associating photo to set: " + si.Filename + "; " + ex2.Message);

                                            if (resp == DialogResult.Abort)
                                            {
                                                SyncAborted = true;
                                                break;
                                            }

                                            if (resp == DialogResult.Ignore)
                                                retry = false;
                                        }
                                        else
                                        {
                                            Thread.Sleep(TimeSpan.FromSeconds(1));
                                        }

                                        retrycount++;
                                    }
                                }
                            }
                            else
                            {
                                try
                                {
                                    Photoset ps = FlickrSync.ri.CreateSet(si.SetTitle, si.SetDescription, PhotoId);
                                    if (ps != null)
                                    {
                                        CurrentSetId = ps.PhotosetId;
                                        string Title = si.SetTitle;
                                        string Description = si.SetDescription;

                                        for (int i = 0; i < SyncItems.Count; i++)
                                        {
                                            SyncItem si2 = (SyncItem)SyncItems[i];

                                            if (si2.SetTitle == Title && si2.SetDescription == Description)
                                            {
                                                si2.SetId = ps.PhotosetId;
                                                si2.SetTitle = "";
                                                si2.SetDescription = "";
                                            }
                                        }

                                        FlickrSync.li.Associate(si.FolderPath, ps.PhotosetId);
                                        NewSets.Add(ps.PhotosetId);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    FlickrSync.Log(FlickrSync.LogLevel.LogBasic, "Error creating set: " + si.SetTitle + "; " + ex.Message);
                                    if (FlickrSync.messages_level!=FlickrSync.MessagesLevel.MessagesAll || MessageBox.Show("Error creating set: " + si.SetTitle + "\n" + ex.Message + "\nDo you want to continue?", "Error", MessageBoxButtons.OKCancel) != DialogResult.OK)
                                    {
                                        SyncAborted = true;
                                        break;
                                    }
                                }

                            }

                            break;

                        case SyncItem.Actions.ActionReplace:
                            try
                            {
                                Flickr.CacheDisabled = true;
                                FlickrSync.ri.ReplacePicture(si.Filename, si.PhotoId, si.Title, si.Description, si.Tags, si.Permission, si.NoDeleteTags, si.GeoLat, si.GeoLong);
                                Flickr.CacheDisabled = false;
                            }
                            catch (Exception ex)
                            {
                                FlickrSync.Error("Error replacing picture: " + si.Filename + " - Skipping this Set", ex, FlickrSync.ErrorType.Normal);
                                ReplaceError = true;
                            }

                            break;

                        case SyncItem.Actions.ActionDelete:
                            try
                            {
                                FlickrSync.ri.DeletePicture(si.PhotoId);
                            }
                            catch (Exception ex)
                            {
                                FlickrSync.Error("Error deleting picture: " + si.PhotoId, ex, FlickrSync.ErrorType.Normal);
                            }
                            break;

                    } // end switch on action

                    FlickrSync.ri.ClearProgress();

                    if (!SyncAborted)
                    {
                        try
                        {
                            pos++;
                            ChangeProgressBar(ProgressBars.PBSync, ProgressValues.PBValue, pos);

                            this.Invoke(new MarkImagesCallBack(MarkCompleted), new object[] { si.item_id, /* success */ true });
                        }
                        catch (Exception)
                        {
                        }

                        if (!ReplaceError && si.Action!=SyncItem.Actions.ActionDelete && fi!=null)
                            SyncProgressDate = fi.LastWriteTime;
                    }
                } // end for each syncItem

                if (!ReplaceError)
                {
                    // CurrentSetId synchronization is finished
                    SyncFolder sfCurrent = GetSyncFolder(CurrentSetId);
                    if (sfCurrent != null)
                    {
                        sfCurrent.LastSync = SyncDate;
                        Sort(sfCurrent);
                    }
                }

                Finish();
            }
            catch (Exception ex)
            {
                FlickrSync.Error("Error during synchronization", ex, FlickrSync.ErrorType.Normal);
                SyncAborted = true;
                Finish();
            }
        }
        #endregion

        private bool ViewLog(int id)
        {
            Process.Start(FlickrSync.LogFile());
            return false;
        }

        private void Finish()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new FinishCallBack(Finish));
            }
            else
            {
                if (Finished)
                    return;

                Finished = true;

                if (SyncStarted)
                {
                    if (ThumbnailThread != null && ThumbnailThread.IsAlive)
                        ThumbnailThread.Abort();

                    string msg;
                    if (SyncAborted)
                        msg = "Synchronization aborted.";
                    else
                        msg = "Synchronization finished.";

                    try
                    {
                        FlickrSync.li.SaveToXML();
                        labelSync.Text = "";

                        FlickrSync.Log(FlickrSync.LogLevel.LogBasic, msg + " Updated configuration saved");

                        if (FlickrSync.messages_level != FlickrSync.MessagesLevel.MessagesNone)
                        {
                            //MessageBox.Show(msg + " Updated configuration saved.", "Sync");
                            CustomMsgBox msgbox = new CustomMsgBox(msg + " Updated configuration saved.","FlickrSync");
                            msgbox.AddButton("OK", 75, 1,msgbox.ButtonCallOK);
                            if (FlickrSync.log_level!=FlickrSync.LogLevel.LogNone)
                                msgbox.AddButton("View Log", 75, 2, ViewLog);
                            msgbox.ShowDialog();
                        }
                    }
                    catch (Exception ex)
                    {
                        FlickrSync.Error(msg + " Error saving configuration", ex, FlickrSync.ErrorType.Normal);
                    }

                    if (NewSets.Count > 0)
                        Program.MainFlickrSync.AddNewSets(NewSets);
                }

                this.Close();
            }
        }

        private void buttonSync_Click(object sender, EventArgs e)
        {
            labelSync.Text = "Synchronizing. Please Wait...";
            buttonSync.Visible=false;

            this.Text = "FlickrSync Synchronizing...0%";

            ThreadStart ts = new ThreadStart(ExecuteSync);
            SyncThread = new Thread(ts);
            SyncStarted = true;
            SyncThread.Name = "UploadPhotosThread";
            SyncThread.Start();
        }

        private void buttonCancel_Click(object sender, EventArgs e)
        {
            SyncAborted = true;
            Finish();
        }

        private void SyncView_FormClosed(object sender, FormClosedEventArgs e)
        {
            SyncAborted = true;
            Finish();
        }

        // listViewToSync_DrawItem is not currently used since listViewToSync is not ownerDraw
        // this is maintained for future extension
        private void listViewToSync_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            Rectangle r=listViewToSync.GetItemRect(e.ItemIndex, ItemBoundsPortion.Icon);
            r.X = r.X + (r.Width - ThumbnailSize) / 2;
            r.Y = r.Y + (r.Height - ThumbnailSize) / 2;
            r.Width = ThumbnailSize;
            r.Height = ThumbnailSize;

            e.Graphics.DrawImage(e.Item.ImageList.Images[e.Item.ImageIndex], r);

            if (listViewToSync.View != View.Details)
            {
                StringFormat fmt = new StringFormat();
                fmt.Trimming = StringTrimming.EllipsisCharacter;
                fmt.FormatFlags = StringFormatFlags.LineLimit;
                fmt.Alignment = StringAlignment.Center;
 
                Rectangle r2 = e.Item.Bounds;
                r2.Y = r.Y + r.Height + 1;
                r2.Height = e.Item.Bounds.Bottom - r2.Y;

                e.Graphics.DrawString(e.Item.Text, listViewToSync.Font, new SolidBrush(e.Item.ForeColor), r2, fmt);
            }
        }
    }
}
