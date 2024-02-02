﻿using Playnite.SDK;
using Playnite.SDK.Models;
using CommonPluginsShared.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommonPluginsControls.Controls;
using CommonPlayniteShared.Common;
using CommonPluginsShared.Interfaces;
using CommonPlayniteShared;
using Playnite.SDK.Data;

namespace CommonPluginsShared.Collections
{
    public abstract class PluginDatabaseObject<TSettings, TDatabase, TItem, T> : ObservableObject, IPluginDatabase
        where TSettings : ISettings
        where TDatabase : PluginItemCollection<TItem>
        where TItem : PluginDataBaseGameBase
    {
        protected static readonly ILogger logger = LogManager.GetLogger();
        protected static IResourceProvider resources = new ResourceProvider();

        public IPlayniteAPI PlayniteApi;
        public TSettings PluginSettings;

        public UI ui = new UI();

        public string PluginName { get; set; }
        public PluginPaths Paths { get; set; }
        public TDatabase Database { get; set; }
        public Game GameContext { get; set; }

        protected string TagBefore = string.Empty;
        public List<Tag> PluginTags { get; set; } = new List<Tag>();


        private bool _isLoaded = false;
        public bool IsLoaded { get => _isLoaded; set => SetValue(ref _isLoaded, value); }

        public bool IsViewOpen = false;

        public bool TagMissing { get; set; } = false;


        public RelayCommand<Guid> GoToGame { get; }


        protected PluginDatabaseObject(IPlayniteAPI PlayniteApi, TSettings PluginSettings, string PluginName, string PluginUserDataPath)
        {
            this.PlayniteApi = PlayniteApi;
            this.PluginSettings = PluginSettings;

            this.PluginName = PluginName;

            Paths = new PluginPaths
            {
                PluginPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                PluginUserDataPath = PluginUserDataPath,
                PluginDatabasePath = Path.Combine(PluginUserDataPath, PluginName),
                PluginCachePath = Path.Combine(PlaynitePaths.DataCachePath, PluginName),
            };
            HttpFileCachePlugin.CacheDirectory = Paths.PluginCachePath;

            FileSystem.CreateDirectory(Paths.PluginDatabasePath);
            FileSystem.CreateDirectory(Paths.PluginCachePath);

            PlayniteApi.Database.Games.ItemUpdated += Games_ItemUpdated;
            PlayniteApi.Database.Games.ItemCollectionChanged += Games_ItemCollectionChanged;

            GoToGame = new RelayCommand<Guid>((Id) =>
            {
                PlayniteApi.MainView.SelectGame(Id);
                PlayniteApi.MainView.SwitchToLibraryView();
            });
        }


        #region Database
        public Task<bool> InitializeDatabase()
        {
            return Task.Run(() =>
            {
                if (IsLoaded)
                {
                    logger.Info($"Database is already initialized");
                    return true;
                }

                IsLoaded = LoadDatabase();

                if (IsLoaded)
                {
                    Database.ItemCollectionChanged += Database_ItemCollectionChanged;
                    Database.ItemUpdated += Database_ItemUpdated;
                }

                return IsLoaded;
            });
        }


        private void Database_ItemUpdated(object sender, ItemUpdatedEventArgs<TItem> e)
        {
            if (GameContext == null)
            {
                return;
            }

            // Publish changes for the currently displayed game if updated
            var ActualItem = e.UpdatedItems.Find(x => x.NewData.Id == GameContext.Id);
            if (ActualItem != null)
            {
                Guid Id = ActualItem.NewData.Id;
                if (Id != null)
                {
                    SetThemesResources(GameContext);
                }
            }
        }

        private void Database_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<TItem> e)
        {
            if (GameContext == null)
            {
                return;
            }

            SetThemesResources(GameContext);
        }


        protected abstract bool LoadDatabase();

        public virtual bool ClearDatabase()
        {
            bool IsOk = false;

            GlobalProgressOptions globalProgressOptions = new GlobalProgressOptions(
                $"{PluginName} - {resources.GetString("LOCCommonProcessing")}",
                false
            );
            globalProgressOptions.IsIndeterminate = false;

            PlayniteApi.Dialogs.ActivateGlobalProgress((activateGlobalProgress) =>
            {
                try
                {
                    List<Game> gamesList = GetGamesList();
                    activateGlobalProgress.ProgressMaxValue = gamesList.Count();

                    foreach (Game game in gamesList)
                    {
                        Remove(game);
                        activateGlobalProgress.CurrentProgressValue++;
                    }

                    IsOk = true;
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, true);
                }

            }, globalProgressOptions);

            return IsOk;
        }

        public virtual void DeleteDataWithDeletedGame()
        {
            var GamesDeleted = Database.Items.Where(x => PlayniteApi.Database.Games.Get(x.Key) == null).Select(x => x).ToList();
            foreach(var el in GamesDeleted)
            {
                logger.Info($"Delete date for missing game: {el.Value.Name} - {el.Key}");
                Database.Remove(el.Key);
            }
        }


        public virtual void GetSelectData()
        {
            var View = new OptionsDownloadData(PlayniteApi);
            Window windowExtension = PlayniteUiHelper.CreateExtensionWindow(PlayniteApi, PluginName + " - " + resources.GetString("LOCCommonSelectData"), View);
            windowExtension.ShowDialog();

            var PlayniteDb = View.GetFilteredGames();
            bool OnlyMissing = View.GetOnlyMissing();

            if (PlayniteDb == null)
            {
                return;
            }

            if (OnlyMissing)
            {
                PlayniteDb = PlayniteDb.FindAll(x => !Get(x.Id, true).HasData);
            }

            GlobalProgressOptions globalProgressOptions = new GlobalProgressOptions(
                $"{PluginName} - {resources.GetString("LOCCommonGettingData")}",
                true
            );
            globalProgressOptions.IsIndeterminate = false;

            PlayniteApi.Dialogs.ActivateGlobalProgress((activateGlobalProgress) =>
            {
            try
            {
                Stopwatch stopWatch = new Stopwatch();
                stopWatch.Start();

                activateGlobalProgress.ProgressMaxValue = (double)PlayniteDb.Count();

                string CancelText = string.Empty;

                    foreach (Game game in PlayniteDb)
                    {
                        if (activateGlobalProgress.CancelToken.IsCancellationRequested)
                        {
                            CancelText = " canceled";
                            break;
                        }

                        Thread.Sleep(10);

                        try
                        {
                            Get(game, false, true);
                        }
                        catch (Exception ex)
                        {
                            Common.LogError(ex, false);
                        }
                        
                        activateGlobalProgress.CurrentProgressValue++;
                    }

                    stopWatch.Stop();
                    TimeSpan ts = stopWatch.Elapsed;
                    logger.Info($"Task GetSelectData(){CancelText} - {string.Format("{0:00}:{1:00}.{2:00}", ts.Minutes, ts.Seconds, ts.Milliseconds / 10)} for {activateGlobalProgress.CurrentProgressValue}/{(double)PlayniteDb.Count()} items");
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, false);
                }
            }, globalProgressOptions);
        }


        public virtual List<Game> GetGamesList()
        {
            List<Game> GamesList = new List<Game>();

            foreach (var item in Database.Items)
            {
                Game game = PlayniteApi.Database.Games.Get(item.Key);

                if (game != null)
                {
                    GamesList.Add(game);
                }
            }

            return GamesList;
        }

        public virtual List<Game> GetGamesWithNoData()
        {
            List<Game> GamesWithNoData = Database.Items.Where(x => !x.Value.HasData).Select(x => PlayniteApi.Database.Games.Get(x.Key)).Where(x => x != null).ToList();
            List<Game> GamesNotInDb = PlayniteApi.Database.Games.Where(x => !Database.Items.Any(y => y.Key == x.Id)).ToList();
            List<Game> mergedList = GamesWithNoData.Union(GamesNotInDb).Distinct().ToList();

            mergedList = mergedList.Where(x => !x.Hidden).ToList();

            return mergedList;
        }


        public virtual List<DataGame> GetDataGames()
        {
            return Database.Items.Select(x => new DataGame
            {
                Id = x.Value.Id,
                Icon = x.Value.Icon.IsNullOrEmpty() ? x.Value.Icon : API.Instance.Database.GetFullFilePath(x.Value.Icon),
                Name = x.Value.Name,
                IsDeleted = x.Value.IsDeleted,
                CountData = x.Value.Count
            }).Distinct().ToList();
        }


        public virtual List<DataGame> GetIsolatedDataGames()
        {
            return Database.Items.Where(x => x.Value.IsDeleted).Select(x => new DataGame
            {
                Id = x.Value.Id,
                Icon = x.Value.Icon.IsNullOrEmpty() ? x.Value.Icon : API.Instance.Database.GetFullFilePath(x.Value.Icon),
                Name = x.Value.Name,
                IsDeleted = x.Value.IsDeleted,
                CountData = x.Value.Count
            }).Distinct().ToList();
        }
        #endregion


        #region Database item methods
        public virtual TItem GetDefault(Guid Id)
        {
            Game game = PlayniteApi.Database.Games.Get(Id);

            if (game == null)
            {
                return null;
            }

            return GetDefault(game);
        }

        public virtual TItem GetDefault(Game game)
        {
            TItem newItem = typeof(TItem).CrateInstance<TItem>();

            newItem.Id = game.Id;
            newItem.Name = game.Name;
            newItem.Game = game;
            newItem.IsSaved = false;

            return newItem;
        }


        public virtual void Add(TItem itemToAdd)
        {
            try
            {
                if (itemToAdd == null)
                {
                    logger.Warn("itemToAdd is null in Add()");
                    return;
                }

                itemToAdd.IsSaved = true;
                Application.Current.Dispatcher?.Invoke(() => Database.Add(itemToAdd), DispatcherPriority.Send);

                // If tag system
                object Settings = PluginSettings.GetType().GetProperty("Settings").GetValue(PluginSettings);
                PropertyInfo propertyInfo = Settings.GetType().GetProperty("EnableTag");

                if (propertyInfo != null)
                {
                    bool EnableTag = (bool)propertyInfo.GetValue(Settings);
                    if (EnableTag)
                    {
                        Common.LogDebug(true, $"RemoveTag & AddTag for {itemToAdd.Name} with {itemToAdd.Id.ToString()}");
                        RemoveTag(itemToAdd.Id, true);
                        AddTag(itemToAdd.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                Common.LogError(ex, false);
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    $"{PluginName}-Error-Add",
                    $"{PluginName}" + System.Environment.NewLine + $"{ex.Message}",
                    NotificationType.Error,
                    () => PlayniteTools.CreateLogPackage(PluginName)
                ));
            }
        }

        public virtual void Update(TItem itemToUpdate)
        {
            try
            {
                if (itemToUpdate == null)
                {
                    logger.Warn("itemToAdd is null in Update()");
                    return;
                }

                itemToUpdate.IsSaved = true;
                Database.Items.TryUpdate(itemToUpdate.Id, itemToUpdate, Get(itemToUpdate.Id, true));
                Application.Current.Dispatcher?.Invoke(() => Database.Update(itemToUpdate), DispatcherPriority.Send);

                // If tag system
                object Settings = PluginSettings.GetType().GetProperty("Settings").GetValue(PluginSettings);
                PropertyInfo propertyInfo = Settings.GetType().GetProperty("EnableTag");

                if (propertyInfo != null)
                {
                    bool EnableTag = (bool)propertyInfo.GetValue(Settings);
                    if (EnableTag)
                    {
                        Common.LogDebug(true, $"RemoveTag & AddTag for {itemToUpdate.Name} with {itemToUpdate.Id.ToString()}");
                        RemoveTag(itemToUpdate.Id, true);
                        AddTag(itemToUpdate.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                Common.LogError(ex, false);
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    $"{PluginName}-Error-Update",
                    $"{PluginName}" + System.Environment.NewLine + $"{ex.Message}",
                    NotificationType.Error,
                    () => PlayniteTools.CreateLogPackage(PluginName)
                ));
            }
        }

        public virtual void AddOrUpdate(TItem item)
        {
            if (item == null)
            {
                logger.Warn("item is null in AddOrUpdate()");
                return;
            }

            var itemCached = GetOnlyCache(item.Id);

            if (itemCached == null)
            {
                Add(item);
            }
            else
            {
                Update(item);
            }
        }


        public virtual void Refresh(Guid Id)
        {
            GlobalProgressOptions globalProgressOptions = new GlobalProgressOptions(
                $"{PluginName} - {resources.GetString("LOCCommonProcessing")}",
                false
            );
            globalProgressOptions.IsIndeterminate = true;

            PlayniteApi.Dialogs.ActivateGlobalProgress((activateGlobalProgress) =>
            {
                RefreshNoLoader(Id);
            }, globalProgressOptions);
        }

        public virtual void Refresh(List<Guid> Ids)
        {
            GlobalProgressOptions globalProgressOptions = new GlobalProgressOptions(
                $"{PluginName} - {resources.GetString("LOCCommonProcessing")}",
                true
            );
            globalProgressOptions.IsIndeterminate = false;

            PlayniteApi.Dialogs.ActivateGlobalProgress((activateGlobalProgress) =>
            {
                Stopwatch stopWatch = new Stopwatch();
                stopWatch.Start();

                activateGlobalProgress.ProgressMaxValue = Ids.Count;

                string CancelText = string.Empty;

                foreach (Guid Id in Ids)
                {
                    if (activateGlobalProgress.CancelToken.IsCancellationRequested)
                    {
                        CancelText = " canceled";
                        break;
                    }

                    RefreshNoLoader(Id);

                    activateGlobalProgress.CurrentProgressValue++;
                }

                stopWatch.Stop();
                TimeSpan ts = stopWatch.Elapsed;
                logger.Info($"Task Refresh(){CancelText} - {string.Format("{0:00}:{1:00}.{2:00}", ts.Minutes, ts.Seconds, ts.Milliseconds / 10)} for {activateGlobalProgress.CurrentProgressValue}/{Ids.Count} items");
            }, globalProgressOptions);
        }

        public virtual void RefreshNoLoader(Guid Id)
        {
            Game game = PlayniteApi.Database.Games.Get(Id);
            logger.Info($"RefreshNoLoader({game?.Name} - {game?.Id})");

            TItem loadedItem = Get(Id, true);
            TItem webItem = GetWeb(Id);

            if (webItem != null && !ReferenceEquals(loadedItem, webItem))
            {
                Update(webItem);
            }
            else
            {
                webItem = loadedItem;
            }

            ActionAfterRefresh(webItem);
        }

        public virtual void RefreshWithNoData(List<Guid> Ids)
        {
            Refresh(Ids);
        }


        public virtual void ActionAfterRefresh(TItem item)
        {

        }

        public virtual PluginDataBaseGameBase MergeData(Guid fromId, Guid toId)
        {
            return null;
        }


        public virtual bool Remove(Game game)
        {
            return Remove(game.Id);
        }

        public virtual bool Remove(Guid Id)
        {
            // If tag system
            var Settings = PluginSettings.GetType().GetProperty("Settings").GetValue(PluginSettings);
            PropertyInfo propertyInfo = Settings.GetType().GetProperty("EnableTag");

            if (propertyInfo != null)
            {
                Common.LogDebug(true, $"RemoveTag for {Id.ToString()}");
                RemoveTag(Id);
            }

            if (Database.Items.ContainsKey(Id))
            {
                return (bool)Application.Current.Dispatcher?.Invoke(() => { return Database.Remove(Id); }, DispatcherPriority.Send);
            }

            return false;
        }

        public virtual bool Remove(List<Guid> Ids)
        {
            // If tag system
            var Settings = PluginSettings.GetType().GetProperty("Settings").GetValue(PluginSettings);
            PropertyInfo propertyInfo = Settings.GetType().GetProperty("EnableTag");

            foreach (Guid Id in Ids)
            {
                if (propertyInfo != null)
                {
                    Common.LogDebug(true, $"RemoveTag for {Id.ToString()}");
                    RemoveTag(Id);
                }

                if (Database.Items.ContainsKey(Id))
                {
                    Application.Current.Dispatcher?.Invoke(() => { Database.Remove(Id); }, DispatcherPriority.Send);
                }
            }

            return true;
        }


        public virtual TItem GetOnlyCache(Guid Id)
        {
            return Database.Get(Id);
        }

        public virtual TItem GetOnlyCache(Game game)
        {
            return Database.Get(game.Id);
        }


        public virtual TItem GetClone(Guid Id)
        {
            return Serialization.GetClone(Get(Id, true, false));
        }

        public virtual TItem GetClone(Game game)
        {
            return Serialization.GetClone(Get(game, true, false));
        }


#pragma warning disable CS1066 // La valeur par défaut spécifiée pour le paramètre n'aura aucun effet, car elle s'applique à un membre utilisé dans des contextes qui n'autorisent pas les arguments facultatifs
        PluginDataBaseGameBase IPluginDatabase.Get(Game game, bool OnlyCache, bool Force = false)
#pragma warning restore CS1066 // La valeur par défaut spécifiée pour le paramètre n'aura aucun effet, car elle s'applique à un membre utilisé dans des contextes qui n'autorisent pas les arguments facultatifs
        {
            return Get(game, OnlyCache, Force);
        }

#pragma warning disable CS1066 // La valeur par défaut spécifiée pour le paramètre n'aura aucun effet, car elle s'applique à un membre utilisé dans des contextes qui n'autorisent pas les arguments facultatifs
        PluginDataBaseGameBase IPluginDatabase.Get(Guid Id, bool OnlyCache, bool Force = false)
#pragma warning restore CS1066 // La valeur par défaut spécifiée pour le paramètre n'aura aucun effet, car elle s'applique à un membre utilisé dans des contextes qui n'autorisent pas les arguments facultatifs
        {
            return Get(Id, OnlyCache, Force);
        }


        PluginDataBaseGameBase IPluginDatabase.GetClone(Game game)
        {
            return GetClone(game);
        }

        PluginDataBaseGameBase IPluginDatabase.GetClone(Guid Id)
        {
            return GetClone(Id);
        }


        void IPluginDatabase.AddOrUpdate(PluginDataBaseGameBase item)
        {
            AddOrUpdate((TItem)item);
        }


        public abstract TItem Get(Guid Id, bool OnlyCache = false, bool Force = false);

        public virtual TItem Get(Game game, bool OnlyCache = false, bool Force = false)
        {
            return Get(game.Id, OnlyCache, Force);
        }


        public virtual TItem GetWeb(Guid Id)
        {
            return null;
        }

        public virtual TItem GetWeb(Game game)
        {
            return GetWeb(game.Id);
        }
        #endregion


        #region Tag system
        public void GetPluginTags()
        {
            PluginTags = new List<Tag>();
            if (!TagBefore.IsNullOrEmpty())
            {
                PluginTags = PlayniteApi.Database.Tags?.Where(x => (bool)x.Name?.StartsWith(TagBefore))?.ToList() ?? new List<Tag>();
            }
        }

        internal Guid? CheckTagExist(string TagName)
        {
            string CompletTagName = TagBefore.IsNullOrEmpty() ? TagName : TagBefore + " " + TagName;

            Guid? FindGoodPluginTags = PluginTags.Find(x => x.Name == CompletTagName)?.Id;
            if (FindGoodPluginTags == null)
            {
                PlayniteApi.Database.Tags.Add(new Tag { Name = CompletTagName });
                GetPluginTags();
                FindGoodPluginTags = PluginTags.Find(x => x.Name == CompletTagName).Id;
            }
            return FindGoodPluginTags;
        }


        public virtual void AddTag(Game game, bool noUpdate = false)
        {
            GetPluginTags();
            TItem item = Get(game, true);

            if (item.HasData)
            {
                try
                {
                    Guid? TagId = FindGoodPluginTags(string.Empty);
                    if (TagId != null)
                    {
                        if (game.TagIds != null)
                        {
                            game.TagIds.Add((Guid)TagId);
                        }
                        else
                        {
                            game.TagIds = new List<Guid> { (Guid)TagId };
                        }

                        if (!noUpdate)
                        {
                            Application.Current.Dispatcher?.Invoke(() =>
                            {
                                PlayniteApi.Database.Games.Update(game);
                                game.OnPropertyChanged();
                            }, DispatcherPriority.Send);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, false, $"Tag insert error with {game.Name}", true, PluginName, string.Format(resources.GetString("LOCCommonNotificationTagError"), game.Name));
                }
            }
            else if (TagMissing)
            {
                if (game.TagIds != null)
                {
                    game.TagIds.Add((Guid)AddNoDataTag());
                }
                else
                {
                    game.TagIds = new List<Guid> { (Guid)AddNoDataTag() };
                }

                if (!noUpdate)
                {
                    Application.Current.Dispatcher?.Invoke(() =>
                    {
                        PlayniteApi.Database.Games.Update(game);
                        game.OnPropertyChanged();
                    }, DispatcherPriority.Send);
                }

            }
        }

        public void AddTag(Guid Id, bool noUpdate = false)
        {
            Game game = PlayniteApi.Database.Games.Get(Id);
            if (game != null)
            {
                AddTag(game, noUpdate);
            }
        }


        public void AddTagAllGame()
        {
            GlobalProgressOptions globalProgressOptions = new GlobalProgressOptions(
                $"{PluginName} - {resources.GetString("LOCCommonAddingAllTag")}",
                true
            );
            globalProgressOptions.IsIndeterminate = false;

            PlayniteApi.Dialogs.ActivateGlobalProgress((activateGlobalProgress) =>
            {
                try
                {
                    Stopwatch stopWatch = new Stopwatch();
                    stopWatch.Start();

                    var PlayniteDb = PlayniteApi.Database.Games.Where(x => x.Hidden == false);
                    activateGlobalProgress.ProgressMaxValue = (double)PlayniteDb.Count();

                    string CancelText = string.Empty;

                    foreach (Game game in PlayniteDb)
                    {
                        if (activateGlobalProgress.CancelToken.IsCancellationRequested)
                        {
                            CancelText = " canceled";
                            break;
                        }

                        Thread.Sleep(10);

                        try
                        { 
                            RemoveTag(game, true);
                            AddTag(game);
                        }
                        catch (Exception ex)
                        {
                            Common.LogError(ex, false);
                        }

                        activateGlobalProgress.CurrentProgressValue++;
                    }

                    stopWatch.Stop();
                    TimeSpan ts = stopWatch.Elapsed;
                    logger.Info($"AddTagAllGame(){CancelText} - {string.Format("{0:00}:{1:00}.{2:00}", ts.Minutes, ts.Seconds, ts.Milliseconds / 10)} for {activateGlobalProgress.CurrentProgressValue}/{(double)PlayniteDb.Count()} items");
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, false);
                }
            }, globalProgressOptions);
        }

        public void AddTagSelectData()
        {
            var View = new OptionsDownloadData(PlayniteApi, true);
            Window windowExtension = PlayniteUiHelper.CreateExtensionWindow(PlayniteApi, PluginName + " - " + resources.GetString("LOCCommonSelectGames"), View);
            windowExtension.ShowDialog();

            var PlayniteDb = View.GetFilteredGames();
            TagMissing = View.GetTagMissing();

            if (PlayniteDb == null)
            {
                return;
            }

            GlobalProgressOptions globalProgressOptions = new GlobalProgressOptions(
                $"{PluginName} - {resources.GetString("LOCCommonAddingAllTag")}",
                true
            );
            globalProgressOptions.IsIndeterminate = false;

            PlayniteApi.Dialogs.ActivateGlobalProgress((activateGlobalProgress) =>
            {
                try
                {
                    Stopwatch stopWatch = new Stopwatch();
                    stopWatch.Start();

                    activateGlobalProgress.ProgressMaxValue = (double)PlayniteDb.Count();

                    string CancelText = string.Empty;

                    foreach (Game game in PlayniteDb)
                    {
                        if (activateGlobalProgress.CancelToken.IsCancellationRequested)
                        {
                            CancelText = " canceled";
                            break;
                        }

                        Thread.Sleep(10);

                        try
                        {
                            RemoveTag(game, true);
                            AddTag(game);
                        }
                        catch (Exception ex)
                        {
                            Common.LogError(ex, false);
                        }

                        activateGlobalProgress.CurrentProgressValue++;
                    }

                    TagMissing = false;

                    stopWatch.Stop();
                    TimeSpan ts = stopWatch.Elapsed;
                    logger.Info($"AddTagSelectData(){CancelText} - {string.Format("{0:00}:{1:00}.{2:00}", ts.Minutes, ts.Seconds, ts.Milliseconds / 10)} for {activateGlobalProgress.CurrentProgressValue}/{(double)PlayniteDb.Count()} items");
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, false);
                }
            }, globalProgressOptions);
        }


        public void RemoveTag(Game game, bool noUpdate = false)
        {
            if (game?.TagIds != null)
            {
                if (PluginTags.Count == 0)
                {
                    GetPluginTags();
                }

                if (game.TagIds.Where(x => PluginTags.Any(y => x == y.Id)).Count() > 0)
                {
                    game.TagIds = game.TagIds.Where(x => !PluginTags.Any(y => x == y.Id)).ToList();
                    if (!noUpdate)
                    {
                        Application.Current.Dispatcher?.Invoke(() =>
                        {
                            PlayniteApi.Database.Games.Update(game);
                            game.OnPropertyChanged();
                        }, DispatcherPriority.Send);
                    }
                }
            }
        }

        public void RemoveTag(Guid Id, bool noUpdate = false)
        {
            Game game = PlayniteApi.Database.Games.Get(Id);
            if (game != null)
            {
                RemoveTag(game, noUpdate);
            }
        }


        public void RemoveTagAllGame(bool FromClearDatabase = false)
        {
            Common.LogDebug(true, "RemoveTagAllGame()");

            string Message = string.Empty;
            if (FromClearDatabase)
            {
                Message = $"{PluginName} - {resources.GetString("LOCCommonClearingAllTag")}";
            }
            else
            {
                Message = $"{PluginName} - {resources.GetString("LOCCommonRemovingAllTag")}";
            }

            GlobalProgressOptions globalProgressOptions = new GlobalProgressOptions(Message, true);
            globalProgressOptions.IsIndeterminate = false;

            PlayniteApi.Dialogs.ActivateGlobalProgress((activateGlobalProgress) =>
            {
                try
                {
                    Stopwatch stopWatch = new Stopwatch();
                    stopWatch.Start();

                    var PlayniteDb = PlayniteApi.Database.Games.Where(x => x.Hidden == false);
                    activateGlobalProgress.ProgressMaxValue = (double)PlayniteDb.Count();

                    string CancelText = string.Empty;

                    foreach (Game game in PlayniteDb)
                    {
                        if (activateGlobalProgress.CancelToken.IsCancellationRequested)
                        {
                            CancelText = " canceled";
                            break;
                        }

                        try
                        { 
                            RemoveTag(game);
                        }
                        catch (Exception ex)
                        {
                            Common.LogError(ex, false);
                        }

                        activateGlobalProgress.CurrentProgressValue++;
                    }

                    stopWatch.Stop();
                    TimeSpan ts = stopWatch.Elapsed;
                    logger.Info($"RemoveTagAllGame(){CancelText} - {string.Format("{0:00}:{1:00}.{2:00}", ts.Minutes, ts.Seconds, ts.Milliseconds / 10)} for {activateGlobalProgress.CurrentProgressValue}/{(double)PlayniteDb.Count()} items");
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, false);
                }
            }, globalProgressOptions);
        }


        public virtual Guid? FindGoodPluginTags(string TagName)
        {
            return CheckTagExist(TagName);
        }
        #endregion


        public void ClearCache()
        {
            string PathDirectory = Path.Combine(PlaynitePaths.DataCachePath, PluginName);

            GlobalProgressOptions globalProgressOptions = new GlobalProgressOptions(
                $"{PluginName} - {resources.GetString("LOCCommonProcessing")}",
                false
            );
            globalProgressOptions.IsIndeterminate = true;

            PlayniteApi.Dialogs.ActivateGlobalProgress((activateGlobalProgress) =>
            {
                try
                {
                    if (Directory.Exists(PathDirectory))
                    {
                        Thread.Sleep(2000);
                        Directory.Delete(PathDirectory, true);
                    }
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, false);
                    PlayniteApi.Dialogs.ShowErrorMessage(
                        string.Format(resources.GetString("LOCCommonErrorDeleteCache"), PathDirectory),
                        PluginName
                    );
                }
            }, globalProgressOptions);
        }


        public virtual void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            if (e?.UpdatedItems != null)
            {
                foreach (var GameUpdated in e.UpdatedItems)
                {
                    Database.SetGameInfo<T>(PlayniteApi, GameUpdated.NewData.Id);
                }
            }
        }

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            if (e?.RemovedItems != null)
            {
                foreach (var GameRemoved in e.RemovedItems)
                {
                    Remove(GameRemoved);
                }
            }
        }

        public Guid? AddNoDataTag()
        {
            return CheckTagExist($"{resources.GetString("LOCNoData")}");
        }

        public virtual void SetThemesResources(Game game)
        {
        }
    }
}
