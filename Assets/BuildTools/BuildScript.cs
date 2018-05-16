#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Diagnostics;
using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using SevenZip;

[ExecuteInEditMode]
public class BuildScript
{
    #region Common

    static string[] SCENES = FindEnabledEditorScenes();
    static string APP_NAME = "game";
    static string DATETIME = DateTime.Now.ToString("yyyy_MM_dd-HH_mm_ss");
    static string TARGET_DIR = "bin/";

    public const string BundleOutDir = "AssetBundle/";

    private static string[] FindEnabledEditorScenes()
    {
        List<string> EditorScenes = new List<string>();
        foreach(EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if(!scene.enabled)
                continue;
            EditorScenes.Add(scene.path);
        }
        return EditorScenes.ToArray();
    }

    static void GenericBuild(string[] scenes, string target_dir, BuildTargetGroup targetGroup, BuildTarget build_target, BuildOptions build_options)
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(targetGroup, build_target);
        try
        {
            BuildPipeline.BuildPlayer(scenes, target_dir, build_target, build_options);
        }
         catch(Exception e)
        {
            AppLog.e("BuildPlayer failure: " + e);
        }

        ProcessStartInfo pi = new ProcessStartInfo(
#if UNITY_EDITOR_WIN
            "explorer.exe",
            TARGET_DIR.wpath()
#elif UNITY_EDITOR_OSX
            "open",
            TARGET_DIR.upath()
#endif
        );
        pi.WorkingDirectory = ".";
        Process.Start(pi);
    }

    public static List<string> ExcludeExtensions = new List<string>()
    {
        ".tmp",
        ".bak",
        ".unity",
        ".meta",
        ".DS_Store",
    };

    public static string TargetName(BuildTarget target)
    {
        switch(target)
        {
        case BuildTarget.Android:
            return "Android";
        case BuildTarget.iOS:
            return "iOS";
        case BuildTarget.StandaloneWindows:
            return "Windows";
        case BuildTarget.StandaloneWindows64:
            return "Windows64";
        case BuildTarget.StandaloneOSX:
        case BuildTarget.StandaloneOSXIntel:
            return "OSX";
        default:
            return "unknown";
        }
    }

    #region AssetBundle
    public static void BuildAssetBundle(BuildTarget targetPlatform, bool rebuild = false)
    {
        var tmp = EditorUserBuildSettings.activeBuildTarget;
        var t = DateTime.Now;
        try
        {
            var outDir = BundleOutDir + TargetName(targetPlatform);
            if(!Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            // backup old manifest
            var oldManifestPath = outDir + "/" + TargetName(targetPlatform);
            if(File.Exists(oldManifestPath))
                File.Copy(oldManifestPath, oldManifestPath + ".old", true);


            // lua
            var luas = Directory.GetFiles(BundleConfig.BundleResRoot, "*.lua", SearchOption.AllDirectories);
            var n = 0;
            foreach(var f in luas)
            {
                EditorUtility.DisplayCancelableProgressBar("copy lua ...", f, (float)(++n) / luas.Length);

                var ftxt = f.Replace(".lua", ".lua.txt");
                File.Copy(f, ftxt, true);
            }
            AssetDatabase.Refresh();

            var options = (
                BuildAssetBundleOptions.None
//              | BuildAssetBundleOptions.UncompressedAssetBundle
              | BuildAssetBundleOptions.ChunkBasedCompression
//              | BuildAssetBundleOptions.ForceRebuildAssetBundle
            );

            if (rebuild)
                options |= BuildAssetBundleOptions.ForceRebuildAssetBundle;
            
            var manifest = BuildPipeline.BuildAssetBundles(
                outDir,
                options,
                targetPlatform
            );

            // zip
            var outRoot = BundleOutDir + TargetName(targetPlatform)
                + "/" + BundleConfig.Instance().Version;
            AssetBundleManifest oldManifest = null;
            if(File.Exists(oldManifestPath + ".old"))
                oldManifest = AssetBundle.LoadFromFile(oldManifestPath + ".old").LoadAsset<AssetBundleManifest>("AssetBundleManifest");
            var allAssetBundles = manifest.GetAllAssetBundles().ToList();
            allAssetBundles.Add(TargetName(targetPlatform));
            n = 0;
            foreach(var i in allAssetBundles)
            {
                var finfo = new FileInfo(outDir + "/" + i);
                // size
                var bundleInfo = BundleConfig.Instance().GetBundleInfo(i);
                if(bundleInfo != null)
                {
                    bundleInfo.Size = (ulong)finfo.Length;
                }

                var hash = manifest.GetAssetBundleHash(i);
                var oldhash = Hash128.Parse("0");
                if(oldManifest != null)
                    oldhash = oldManifest.GetAssetBundleHash(i);
                var path = outDir + "/" + i;
                var lzmaPath = path + BundleConfig.CompressedExtension;
                if(hash != oldhash || !File.Exists(lzmaPath))
                {
                    EditorUtility.DisplayCancelableProgressBar("compressing ...", i, (float)(++n) / allAssetBundles.Count);
                    AppLog.d("update: {0}:{1}:{2}", i, hash, oldhash);

                    // TODO: encode bundle
                    BundleHelper.CompressFileLZMA(path, lzmaPath);
                }

                // copy
                var outPath = outRoot + "/" + i + BundleConfig.CompressedExtension;
                var dir = Path.GetDirectoryName(outPath);
                if(!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(lzmaPath, outPath, true);
            }
        }
        finally
        {
//            foreach (var f in Directory.GetFiles(BundleConfig.BundleResRoot, "*.lua.txt*", SearchOption.AllDirectories))
//            {
//                File.Delete(f);
//            }
            AssetDatabase.Refresh();

            EditorUtility.ClearProgressBar();
            AppLog.d("BuildAssetBundle coast: {0}", DateTime.Now - t);
        }
    }

    public static void BuildBundleGroup(BundleConfig.GroupInfo group, BuildTarget targetPlatform, bool rebuild = false)
    {
        var indir = BundleConfig.BundleResRoot + group.Name;
        try
        {
            // lua
            foreach(var f in Directory.GetFiles(indir, "*.lua", SearchOption.AllDirectories))
            {
                var ftxt = f.Replace(".lua", ".lua.txt");
                File.Copy(f, ftxt, true);
                AssetDatabase.ImportAsset(ftxt);
            }

            // Create the array of bundle build details.
            var buildMap = new List<AssetBundleBuild>();
            float count = 0;
            var dirs = Directory.GetDirectories(indir, "*", SearchOption.TopDirectoryOnly);
            foreach(var dir in dirs)
            {
                ++count;
                var udir = dir.upath();
                var assetBundleName = udir.Substring(udir.LastIndexOf('/')+1);
                AppLog.d("pack: {0}=>{1}", dir, assetBundleName);
                var ab = CreateAssetBundleBuild(udir, assetBundleName, ExcludeExtensions, rebuild);
                if(ab != null)
                    buildMap.Add(ab.Value);
                EditorUtility.DisplayCancelableProgressBar("BuildBundle ...", udir, count / dirs.Length);
            }
            if(buildMap.Count == 0)
                return;

            var outdir = BundleOutDir + TargetName(targetPlatform) + "/" + group.Name;
            if(!Directory.Exists(outdir))
            {
                Directory.CreateDirectory(outdir);
            }
            var options = (
                    BuildAssetBundleOptions.None
//                  | BuildAssetBundleOptions.UncompressedAssetBundle
                  | BuildAssetBundleOptions.ChunkBasedCompression
            );
            var manifest = BuildPipeline.BuildAssetBundles(
                outdir,
                buildMap.ToArray(),
                options,
                targetPlatform
            );

            var outRoot = BundleOutDir + TargetName(targetPlatform)
                  + "/" + BundleConfig.Instance().Version + "/" + group.Name;
            if(!Directory.Exists(outRoot))
            {
                Directory.CreateDirectory(outRoot);
            }

            var bundles = manifest.GetAllAssetBundles().ToList();
            bundles.Add(group.Name);
            foreach(var i in bundles)
            {
                var path = outdir + "/" + i;
                var lzmaPath = path + BundleConfig.CompressedExtension;
                // TODO: encode bundle
                BundleHelper.CompressFileLZMA(path, lzmaPath);

                var outPath = outRoot + "/" + i + BundleConfig.CompressedExtension;
                File.Copy(lzmaPath, outPath, true);
            }

            //Compress(outdir, targetPlatform);
        }
        finally
        {
            foreach(var f in Directory.GetFiles(indir, "*.lua.txt*", SearchOption.AllDirectories))
            {
                File.Delete(f);
            }
            EditorUtility.ClearProgressBar();
        }
    }

    static AssetBundleBuild? CreateAssetBundleBuild(string assetDir, string assetBundleName, List<string> excludes, bool rebuild = false)
    {
        var ab = new AssetBundleBuild();
        ab.assetBundleName = assetBundleName + BundleConfig.BundlePostfix;

        var bundleInfo = BundleConfig.Instance().GetBundleInfo(assetDir.Replace(BundleConfig.BundleResRoot, "") + BundleConfig.BundlePostfix);
        long lastBuildTime = long.Parse(bundleInfo.BuildTime);

        var assetNames = new List<string>();
        int nnew = 0;
        foreach(var f in Directory.GetFiles(assetDir, "*.*", SearchOption.AllDirectories))
        {
            if(excludes.Contains(Path.GetExtension(f)))
                continue;
            assetNames.Add(f.upath());

            var finfo = new FileInfo(f);
            if(rebuild || finfo.LastWriteTime.ToFileTime() > lastBuildTime)
            {
                ++nnew;
                 //AppLog.d(f.upath() + ": " + DateTime.FromFileTime(modifyTime));
            }
        }

        if(nnew > 0 && assetNames.Count() > 0)
        {
            ab.assetNames = assetNames.ToArray();
            bundleInfo.BuildTime = DateTime.Now.ToFileTime().ToString();
            bundleInfo.Version = BundleConfig.Instance().Version.ToString();
//            AppLog.d(assetDir + " > " + DateTime.FromFileTime(long.Parse(bundleInfo.BuildTime)));
            return ab;
        }
        else
        {
            return null;
        }
    }

    #endregion AssetBundle

    public static void StreamingSceneBuild(string scene, string path, BuildTarget targetPlatform)
    {
        StreamingSceneBuild(new[] { scene }, path, targetPlatform);
    }
    public static void StreamingSceneBuild(string[] scenes, string outName, BuildTarget targetPlatform)
    {
        string SceneOutPath = BundleOutDir + TargetName(targetPlatform) + "/" + BundleConfig.Instance().Version + "/Level/" + outName + ".fg";

        var dir = Path.GetDirectoryName(SceneOutPath);
        if(!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        BuildPipeline.BuildPlayer(
            scenes,
            SceneOutPath,
            targetPlatform,
            BuildOptions.BuildAdditionalStreamedScenes);
    }

    #endregion Common

    #region 转表
    [MenuItem("Tools/Open Xls Folder")]
    static void OpenExcelDir()
    {
        ProcessStartInfo pi = new ProcessStartInfo(
#if UNITY_EDITOR_WIN
            "explorer.exe",
#elif UNITY_EDITOR_OSX
            "open",
#endif
            "..\\table"
        );
        pi.WorkingDirectory = ".";
        Process.Start(pi);
    }

    [MenuItem("Tools/Open LuaTable Folder")]
    static void OpenLuaDir()
    {
        ProcessStartInfo pi = new ProcessStartInfo(
#if UNITY_EDITOR_WIN
            "explorer.exe",
            "Assets\\BundleRes\\lua\\table"
#elif UNITY_EDITOR_OSX
            "open",
            "Assets/BundleRes/lua/table"
#endif
        );
        pi.WorkingDirectory = ".";
        Process.Start(pi);
    }


    [MenuItem("Tools/Auto Convert Xls to Lua")]
    static void ConvertXlsToLua()
    {
        Console.WriteLine("");
        ProcessStartInfo pi = new ProcessStartInfo(
#if UNITY_EDITOR_WIN
            "Convert.exe",
            "../../table ../../client/Assets/BundleRes/Lua/Table"
#elif UNITY_EDITOR_OSX
            "mono",
            "Convert.exe ../../table ../../client/Assets/BundleRes/lua/table"
#endif
        );
        pi.WorkingDirectory = "../tools/table/";
        Process.Start(pi);

        //LuaHelper.Init();
    }

    [MenuItem("Tools/Open GUI Xls Convert Tool")]
    static void ConvertXlsToLuaGUI()
    {
        Console.WriteLine("");
        ProcessStartInfo pi = new ProcessStartInfo(
            "GameManager.exe"
        );
        pi.WorkingDirectory = "../tools/table";
        Process.Start(pi);
    }
    #endregion 转表

    #region 安卓安装包
    [MenuItem("Build/AndroidApk")]
    static void BuildAndroidApk()
    {
        var version = BundleConfig.Instance().Version;
        // TODO: open this when release
        // version.Minor += 1;
        version.Patch = 0;
        PlayerSettings.bundleVersion = version.ToString();
        PlayerSettings.Android.bundleVersionCode += 1;
        var versionCode = PlayerSettings.Android.bundleVersionCode;

        string target_dir = TARGET_DIR + APP_NAME + ".apk";
        GenericBuild(SCENES, target_dir, BuildTargetGroup.Android, BuildTarget.Android, BuildOptions.None);
    }

    #endregion 安卓打包

    #region 🍎安装包

    [MenuItem("Build/iOS (iL2cpp proj)")]
    static void BuildIosIL2cppProj()
    {
        var version = BundleConfig.Instance().Version;
        //version.Minor += 1;
        version.Patch = 0;
        PlayerSettings.bundleVersion = version.ToString();
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
        string target_dir = Environment.GetEnvironmentVariable("IosProjDir");
        if (string.IsNullOrEmpty(target_dir))
        {
            target_dir = "ios.proj";
        }
        var option = BuildOptions.EnableHeadlessMode 
            | BuildOptions.SymlinkLibraries 
            //| BuildOptions.Il2CPP
            | BuildOptions.AcceptExternalModificationsToPlayer
            ;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
        version.Patch = 0;
        if(Environment.GetEnvironmentVariable("configuration") == "Release")
        {
            PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.None);
        }
        else
        {
            PlayerSettings.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
            option |= BuildOptions.AllowDebugging;
        }
        GenericBuild(SCENES, target_dir, BuildTargetGroup.iOS, BuildTarget.iOS, option);
    }
    [MenuItem("Build/iOS (iL2cpp proj sim)")]
    static void BuildIosIL2cppProjSim()
    {
        var version = BundleConfig.Instance().Version;
        PlayerSettings.bundleVersion = version.ToString();
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.SimulatorSDK;
//        PlayerSettings.iOS.buildNumber = (int.Parse(PlayerSettings.iOS.buildNumber) + 1).ToString();
        var versionCode = int.Parse(PlayerSettings.iOS.buildNumber);

        string target_dir = Environment.GetEnvironmentVariable("IosProjDir");
        if(string.IsNullOrEmpty(target_dir))
        {
            target_dir = "ios.proj.sim";
        }

        var option = BuildOptions.EnableHeadlessMode 
            | BuildOptions.SymlinkLibraries 
            //| BuildOptions.Il2CPP
//            | BuildOptions.Development
            | BuildOptions.AcceptExternalModificationsToPlayer
            | BuildOptions.AllowDebugging
        ;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
        version.Patch = 0;
        if(Environment.GetEnvironmentVariable("configuration") == "Release")
        {
            PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.None);
        }
        else
        {
            PlayerSettings.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
            option |= BuildOptions.AllowDebugging;
        }
        GenericBuild(SCENES, target_dir, BuildTargetGroup.iOS, BuildTarget.iOS, option);
    }

    [MenuItem("Build/Mac OS X")]
    static void BuildMacOSX()
    {
        string target_dir = TARGET_DIR + "/" + APP_NAME + "-" + DATETIME + ".app";
        GenericBuild(SCENES, target_dir, BuildTargetGroup.Standalone, BuildTarget.StandaloneOSXIntel, BuildOptions.None);
    }


    [MenuItem("Build/iOS/AssetBundle"), ExecuteInEditMode]
    public static void BuildiOSAssetBundle()
    {
        AssetBundleBuildAll(BuildTarget.iOS);
        //StreamingSceneBuild(BuildTarget.Android);

        //// compress
        //Compress(BundleOutDir + TargetName(BuildTarget.Android) + "/" + PlayerSettings.bundleVersion, BuildTarget.Android);

        GenBundleManifest(BuildTarget.iOS);

        // version 
        GenVersionFile(BuildTarget.iOS);

        // TODO: upload to http server
    }


    [MenuItem("Build/iOS/Manifest")]
    public static void BuildiOSManifest()
    {
        GenBundleManifest(BuildTarget.iOS);
        EditorUtility.ClearProgressBar();
    }
    #endregion 🍎打包

    #region Windows

    [MenuItem("Build/Windows")]
    static void BuildWindows()
    {
        string target_dir = TARGET_DIR + "/" + DATETIME + "/" + APP_NAME + ".exe";
        GenericBuild(SCENES, target_dir, BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows, BuildOptions.None);
    }

    #endregion Windows

    #region 安卓资源包

    [MenuItem("Build/Android/AssetBundle"), ExecuteInEditMode]
    public static void BuildAndroidAssetBundle()
    {
        AssetBundleBuildAll(BuildTarget.Android);
        //StreamingSceneBuild(BuildTarget.Android);

        //// compress
        //Compress(BundleOutDir + TargetName(BuildTarget.Android) + "/" + PlayerSettings.bundleVersion, BuildTarget.Android);

        GenBundleManifest(BuildTarget.Android);

        // version 
        GenVersionFile(BuildTarget.Android);

        // TODO: upload to http server
    }

    public static void Compress(string indir, BuildTarget targetPlatform)
    {
        try
        {
            var outRoot = BundleOutDir + TargetName(targetPlatform)
                  + "/" + BundleConfig.Instance().Version;
            if(!Directory.Exists(outRoot))
            {
                Directory.CreateDirectory(outRoot);
            }

            // compress
            var files = (from f in Directory.GetFiles(indir, "*", SearchOption.AllDirectories)
                         where Path.GetExtension(f) != ".manifest"
                         && Path.GetExtension(f) != BundleConfig.CompressedExtension
                         || Path.GetExtension(f) == ""
                         select f.upath()).ToArray();
            int i = 0;
            foreach(var f in files)
            {
                ++i;
                EditorUtility.DisplayCancelableProgressBar("compressing ...", f, (float)(i) / files.Length);

                BundleHelper.CompressFileLZMA(f, f.Replace(BundleOutDir + TargetName(targetPlatform), outRoot) + BundleConfig.CompressedExtension);
                //File.Delete(f);
            }
        }
        catch(Exception e)
        {
            AppLog.e(e);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    public static void GenVersionFile(BuildTarget buildTarget)
    {
        var versionUrl = BundleOutDir + TargetName(buildTarget) + "/resversion.txt";
        StreamWriter writer = new StreamWriter(versionUrl);
        var version = BundleConfig.Instance().Version;
        writer.Write(version.ToString());
        writer.Close();
        File.Copy(versionUrl, BundleOutDir + TargetName(buildTarget) + "/" + version + "/resversion.txt", true);
    }

    [MenuItem("Build/Android/Manifest")]
    public static void BuildAndroidManifest()
    {
        GenBundleManifest(BuildTarget.Android);
        EditorUtility.ClearProgressBar();
    }

    // TODO: replace to Build BundleConfig
    public static void GenBundleManifest(BuildTarget buildTarget)
    {
        try
        {
            var version = BundleConfig.Instance().Version;
            var rootDir = BundleOutDir + TargetName(buildTarget) + "/";
            var sourceDir = rootDir + version.ToString() + "/";
            if(!Directory.Exists(sourceDir))
            {
                Directory.CreateDirectory(sourceDir);
            }

            var manifestbf = sourceDir + BundleConfig.ManifestName + BundleConfig.CompressedExtension;
            if(File.Exists(manifestbf))
            {
                File.Delete(manifestbf);
            }

            var files = Directory.GetFiles(sourceDir, "*" + BundleConfig.CompressedExtension, SearchOption.AllDirectories);
            float i = 0;
            foreach(var f in files)
            {
                ++i;
                var bf = f.upath().Replace(version.ToString() + "/", string.Empty).Replace(BundleConfig.CompressedExtension, string.Empty);
                var md5 = BundleHelper.Md5(bf);
                var subPath = bf.Replace(rootDir, string.Empty);
                var bundleInfo = BundleConfig.Instance().GetBundleInfo(subPath);
                if(bundleInfo != null)
                {
                    bundleInfo.Md5 = md5;
                }
                else
                {
                    bundleInfo = new BundleConfig.BundleInfo()
                    {
                        Name = subPath,
                        Md5 = md5,
                        Version = version.ToString(),
                    };
                }
            }
            BundleConfig.Instance().Save();

            // generate md5 sheet
            var manifestPath = rootDir + BundleConfig.ManifestName;
            if(File.Exists(manifestPath))
            {
                File.Copy(manifestPath, manifestPath + ".bak", true);
            }
            YamlHelper.Serialize(BundleConfig.Instance().Groups, manifestPath);
            BundleHelper.CompressFileLZMA(manifestPath, manifestbf);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    public static void AssetBundleBuildAll(BuildTarget buildTarget)
    {
        try
        {
            var version = BundleConfig.Instance().Version;
            // TODO: open this when release
            // version.Patch += 1;
            PlayerSettings.bundleVersion = version.ToString();
            //AndroidAssetBundleDelete();
            foreach(var i in BundleConfig.Instance().Groups)
            {
                BuildBundleGroup(i, buildTarget, true);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    #region AndroidStreamingScene
    [MenuItem("Build/Android/StreamingScene"), ExecuteInEditMode]
    public static void BuildAndroidStreamingScene()
    {
        StreamingSceneBuild(BuildTarget.Android);
    }

    public static void StreamingSceneBuild(BuildTarget buildTarget)
    {
        try
        {
            foreach(var StreamSceneDir in new string[]{ "Scene" } )
            {
                float count = 0;
                var files = Directory.GetFiles(BundleConfig.BundleResRoot + StreamSceneDir, "*.unity", SearchOption.AllDirectories);
                foreach(var i in files)
                {
                    var f = i.upath();
                    AppLog.d(f);
                    EditorUtility.DisplayCancelableProgressBar("StreamingScene ...", f, count / files.Length);
                    StreamingSceneBuild(
                        new[] { f },
                        Path.GetFileNameWithoutExtension(f),
                        buildTarget);
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }
    #endregion

    #endregion 安卓资源包
}
#endif
