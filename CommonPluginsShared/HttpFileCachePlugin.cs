﻿using CommonPlayniteShared;
using CommonPlayniteShared.Common;
using CommonPlayniteShared.Common.Web;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace CommonPluginsShared
{
    public class HttpFileCachePlugin
    {
        private static object cacheLock { get; set; } = new object();

        public static string CacheDirectory { get; set; } = PlaynitePaths.ImagesCachePath;

        private static string GetFileNameFromUrl(string url)
        {
            var uri = new Uri(url);
            var extension = Path.GetExtension(uri.Segments[uri.Segments.Length - 1]);
            var md5 = url.MD5();
            return md5 + extension;
        }

        public static bool FileWebIsCached(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return true;
            }
            if (!StringExtensions.IsHttpUrl(url)) 
            {
                return true;
            }

            string cacheFile = Path.Combine(CacheDirectory, GetFileNameFromUrl(url));
            return File.Exists(cacheFile) && new FileInfo(cacheFile).Length != 0;
        }

        public static string GetWebFile(string url, int resize = 0)
        {
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }

            var cacheFile = Path.Combine(CacheDirectory, GetFileNameFromUrl(url));
            lock (cacheLock)
            {
                if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length != 0)
                {
                    //logger.Debug($"Returning {url} from file cache {cacheFile}.");
                    return cacheFile;
                }
                else
                {
                    FileSystem.CreateDirectory(CacheDirectory);

                    try
                    {
                        if (resize > 0)
                        {
                            string tmpPath = Path.Combine(PlaynitePaths.ImagesCachePath, Path.GetFileName(cacheFile));
                            HttpDownloader.DownloadFile(url, tmpPath);
                            ImageTools.Resize(tmpPath, resize, resize, cacheFile);
                            FileSystem.DeleteFileSafe(tmpPath);
                        }
                        else
                        {
                            HttpDownloader.DownloadFile(url, cacheFile);
                        }

                        return cacheFile;
                    }
                    catch (WebException e)
                    {
                        if (e.Response == null)
                        {
                            throw;
                        }

                        var response = (HttpWebResponse)e.Response;
                        if (response.StatusCode != HttpStatusCode.NotFound)
                        {
                            throw;
                        }
                        else
                        {
                            return string.Empty;
                        }
                    }
                }
            }
        }

        public static void ClearCache(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            lock (cacheLock)
            {
                var cacheFile = Path.Combine(CacheDirectory, GetFileNameFromUrl(url));
                if (File.Exists(cacheFile))
                {
                    //logger.Debug($"Removing {url} from file cache: {cacheFile}");
                    try
                    {
                        FileSystem.DeleteFileSafe(cacheFile);
                    }
                    catch (Exception e) //when (!PlayniteEnvironment.ThrowAllErrors)
                    {
                        logger.Error(e, $"Failed to remove {url} from cache.");
                    }
                }
            }
        }
    }
}
