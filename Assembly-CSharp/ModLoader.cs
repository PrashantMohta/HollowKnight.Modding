﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

using Object = UnityEngine.Object;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace Modding
{
    /// <summary>
    /// Handles loading of mods.
    /// </summary>
    [SuppressMessage("ReSharper", "SuggestVarOrType_SimpleTypes")]
    [PublicAPI]
    internal static class ModLoader
    {
        /// <summary>
        /// Loads the mod by searching for assemblies in hollow_knight_Data\Managed\Mods\
        /// </summary>
        public static IEnumerator LoadMods(GameObject coroutineHolder)
        {
            if (Loaded)
            {
                Object.Destroy(coroutineHolder);
                yield break;
            }

            Logger.Log("[API] - Trying to load mods");
            string path = string.Empty;
            if (SystemInfo.operatingSystem.Contains("Windows"))
                path = Application.dataPath + "\\Managed\\Mods";
            else if (SystemInfo.operatingSystem.Contains("Mac"))
                path = Application.dataPath + "/Resources/Data/Managed/Mods/";
            else if (SystemInfo.operatingSystem.Contains("Linux"))
                path = Application.dataPath + "/Managed/Mods";
            else
                Logger.LogWarn($"Operating system of {SystemInfo.operatingSystem} is not known.  Unable to load mods.");

            if (string.IsNullOrEmpty(path))
            {
                Loaded = true;
                Object.Destroy(coroutineHolder);
                yield break;
            }

            foreach (string text2 in Directory.GetFiles(path, "*.dll"))
            {
                Logger.LogDebug("[API] - Loading assembly: " + text2);
                try
                {
                    foreach (Type type in Assembly.LoadFile(text2).GetExportedTypes())
                    {
                        if (IsSubclassOfRawGeneric(typeof(Mod<>), type))
                        {
                            Logger.LogDebug("[API] - Trying to instantiate mod<T>: " + type);

                            IMod mod = Activator.CreateInstance(type) as IMod;
                            if (mod == null) continue;
                            LoadedMods.Add((Mod)mod);
                        }
                        else if (!type.IsGenericType && type.IsClass && type.IsSubclassOf(typeof(Mod)))
                        {
                            Logger.LogDebug("[API] - Trying to instantiate mod: " + type);
                            Mod mod2 = type.GetConstructor(new Type[0])?.Invoke(new object[0]) as Mod;
                            if (mod2 == null) continue;
                            LoadedMods.Add(mod2);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - Error: " + ex);
                    Errors.Add(string.Concat(text2, ": FAILED TO LOAD! Check ModLog.txt."));
                }
            }
            
            ModHooks.Instance.LoadGlobalSettings();

            List<string> scenes = new List<string>();
            for (int i = 0; i < USceneManager.sceneCountInBuildSettings; i++)
            {
                string scenePath = SceneUtility.GetScenePathByBuildIndex(i);
                scenes.Add(Path.GetFileNameWithoutExtension(scenePath));
            }

            IEnumerable orderedMods = LoadedMods.OrderBy(x => x.LoadPriority());

            // dict<scene name, list<(mod, list<objectNames>)>
            Dictionary<string, List<(IMod, List<string>)>> toPreload = new Dictionary<string, List<(IMod, List<string>)>>();

            // dict<mod, dict<scene, dict<objName, object>>>
            Dictionary<IMod, Dictionary<string, Dictionary<string, GameObject>>> preloadedObjects = new Dictionary<IMod, Dictionary<string, Dictionary<string, GameObject>>>();

            Logger.Log("[API] - Preloading");

            // Setup dict of scene preloads
            foreach (IMod mod in orderedMods)
            {
                Logger.Log($"[API] - Checking preloads for mod \"{mod.GetName()}\"");

                List<(string, string)> preloadNames = mod.GetPreloadNames();
                if (preloadNames == null)
                {
                    continue;
                }

                // dict<scene, list<objects>>
                Dictionary<string, List<string>> modPreloads = new Dictionary<string, List<string>>();

                foreach ((string scene, string obj) in preloadNames)
                {
                    if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(obj))
                    {
                        Logger.LogWarn($"[API] - Mod \"{mod.GetName()}\" passed null values to preload");
                        continue;
                    }

                    if (!scenes.Contains(scene))
                    {
                        Logger.LogWarn($"[API] - Mod \"{mod.GetName()}\" attempted preload from non-existent scene \"{scene}\"");
                        continue;
                    }

                    if (!modPreloads.TryGetValue(scene, out List<string> objects))
                    {
                        objects = new List<string>();
                        modPreloads[scene] = objects;
                    }

                    Logger.Log($"[API] - Found object \"{scene}.{obj}\"");

                    objects.Add(obj);
                }

                foreach (KeyValuePair<string, List<string>> pair in modPreloads)
                {
                    if (!toPreload.TryGetValue(pair.Key, out List<(IMod, List<string>)> scenePreloads))
                    {
                        scenePreloads = new List<(IMod, List<string>)>();
                        toPreload[pair.Key] = scenePreloads;
                    }

                    scenePreloads.Add((mod, pair.Value));
                    toPreload[pair.Key] = scenePreloads;
                }
            }

            // Create a blanker so the preloading is invisible (but still audible TODO)
            GameObject blanker = CanvasUtil.CreateCanvas(RenderMode.ScreenSpaceOverlay, Vector2.one);
            Object.DontDestroyOnLoad(blanker);

            CanvasUtil.CreateImagePanel(
                blanker,
                CanvasUtil.NullSprite(new byte[] { 0x00, 0x00, 0x00, 0xFF }),
                new CanvasUtil.RectData(Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one))
                .GetComponent<Image>().preserveAspect = false;

            // Preload all needed objects
            foreach (string sceneName in toPreload.Keys)
            {
                Logger.Log($"[API] - Loading scene \"{sceneName}\"");

                USceneManager.LoadScene(sceneName, LoadSceneMode.Single);

                // LoadScene takes 2 frames to work
                yield return new WaitForEndOfFrame();
                yield return new WaitForEndOfFrame();

                Scene scene = USceneManager.GetSceneByName(sceneName);
                GameObject[] rootObjects = scene.GetRootGameObjects();

                // Fetch object names to preload
                List<(IMod, List<string>)> sceneObjects = toPreload[sceneName];

                foreach ((IMod mod, List<string> objNames) in sceneObjects)
                {
                    Logger.Log($"[API] - Fetching objects for mod \"{mod.GetName()}\"");

                    foreach (string objName in objNames)
                    {
                        Logger.Log($"[API] - Fetching object \"{objName}\"");

                        // Split object name into root and child names based on '/'
                        string rootName = null;
                        string childName = null;

                        int slashIndex = objName.IndexOf('/');
                        if (slashIndex == -1)
                        {
                            rootName = objName;
                        }
                        else if (slashIndex == 0 || slashIndex == objName.Length - 1)
                        {
                            Logger.LogWarn($"Invalid preload object name given by mod \"{mod.GetName()}\": \"{objName}\"");
                            continue;
                        }
                        else
                        {
                            rootName = objName.Substring(0, slashIndex);
                            childName = objName.Substring(slashIndex + 1);
                        }

                        // Get root object
                        GameObject obj = rootObjects.FirstOrDefault(o => o.name == rootName);
                        if (obj == null)
                        {
                            Logger.LogWarn($"Could not find object \"{objName}\" in scene \"{sceneName}\", requested by mod \"{mod.GetName()}\"");
                            continue;
                        }

                        // Get child object
                        if (childName != null)
                        {
                            Transform t = obj.transform.Find(childName);
                            if (t == null)
                            {
                                Logger.LogWarn($"Could not find object \"{objName}\" in scene \"{sceneName}\", requested by mod \"{mod.GetName()}\"");
                                continue;
                            }

                            obj = t.gameObject;
                        }

                        // Create all sub-dictionaries if necessary (Yes, it's terrible)
                        if (!preloadedObjects.TryGetValue(mod, out Dictionary<string, Dictionary<string, GameObject>> modPreloadedObjects))
                        {
                            modPreloadedObjects = new Dictionary<string, Dictionary<string, GameObject>>();
                            preloadedObjects[mod] = modPreloadedObjects;
                        }

                        if (!modPreloadedObjects.TryGetValue(sceneName, out Dictionary<string, GameObject> modScenePreloadedObjects))
                        {
                            modScenePreloadedObjects = new Dictionary<string, GameObject>();
                            modPreloadedObjects[sceneName] = modScenePreloadedObjects;
                        }

                        // Create inactive duplicate of requested object
                        obj = Object.Instantiate(obj);
                        Object.DontDestroyOnLoad(obj);
                        obj.SetActive(false);

                        // Set object to be passed to mod
                        modScenePreloadedObjects[objName] = obj;
                    }
                }
            }

            // Reload the main menu to fix the music/shaders
            Logger.Log("[API] - Preload done, returning to main menu");
            yield return GameManager.instance.ReturnToMainMenu(GameManager.ReturnToMainMenuSaveModes.DontSave);

            // Remove the black screen
            Object.Destroy(blanker);

            foreach (IMod mod in orderedMods)
            {
                try
                {
                    preloadedObjects.TryGetValue(mod, out Dictionary<string, Dictionary<string, GameObject>> preloads);
                    LoadMod(mod, false, false, preloads);
                }
                catch (Exception ex)
                {
                    Errors.Add(string.Concat(mod.GetName(), ": FAILED TO LOAD! Check ModLog.txt."));
                    Logger.LogError("[API] - Error: " + ex);
                }
            }

            //Clean out the ModEnabledSettings for any mods that don't exist.
            //Calling ToList means we are not working with the dictionary keys directly, preventing an out of sync error
            foreach (string modName in ModHooks.Instance.GlobalSettings.ModEnabledSettings.Keys.ToList())
            {
                if (LoadedMods.All(x => x.GetName() != modName))
                    ModHooks.Instance.GlobalSettings.ModEnabledSettings.Remove(modName);
            }
            
            // Get previously disabled mods and disable them.
            foreach (KeyValuePair<string, bool> modPair in ModHooks.Instance.GlobalSettings.ModEnabledSettings.Where(x => !x.Value))
            {
                IMod mod = LoadedMods.FirstOrDefault(x => x.GetName() == modPair.Key);
                if (!(mod is ITogglableMod togglable)) continue;
                togglable.Unload();
                Logger.LogDebug($"Mod {modPair.Key} was unloaded.");
            }
            
            GameObject gameObject = new GameObject();
            _draw = gameObject.AddComponent<ModVersionDraw>();
            Object.DontDestroyOnLoad(gameObject);
            UpdateModText();
            Loaded = true;
            
            ModHooks.Instance.SaveGlobalSettings();

            Object.Destroy(coroutineHolder.gameObject);
        }

        private static readonly List<string> Errors = new List<string>();

        static ModLoader()
        {
            Loaded = false;
            Debug = true;
        }

        private static ModVersionDraw _draw;

        private static void UpdateModText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Modding API: " + ModHooks.Instance.ModVersion + (ModHooks.Instance.IsCurrent ? "" : " - New Version Available!") );
            foreach (string error in Errors)
            {
                builder.AppendLine(error);
            }

            // 56 you made me do this, I hope you're happy
            Dictionary<string, List<IMod>> modsByNamespace = new Dictionary<string, List<IMod>>();

            foreach (IMod mod in LoadedMods)
            {
                try
                {
                    if (!ModHooks.Instance.GlobalSettings.ModEnabledSettings[mod.GetName()])
                    {
                        continue;
                    }

                    if (!ModVersionsCache.ContainsKey(mod.GetName()))
                    {
                        ModVersionsCache.Add(mod.GetName(), mod.GetVersion() + (mod.IsCurrent() ? string.Empty : " - New Version Available!"));
                    }

                    string ns = mod.GetType().Namespace;

                    // ReSharper disable once AssignNullToNotNullAttribute
                    if (!modsByNamespace.TryGetValue(ns, out List<IMod> nsMods))
                    {
                        nsMods = new List<IMod>();
                        modsByNamespace.Add(ns, nsMods);
                    }

                    nsMods.Add(mod);
                }
                catch (Exception e)
                {
                    Logger.LogError($"[API] - Failed to obtain mod namespace:\n{e}");
                }
            }

            foreach (string ns in modsByNamespace.Keys)
            {
                try
                {
                    List<IMod> nsMods = modsByNamespace[ns];

                    if (nsMods == null || nsMods.Count == 0)
                    {
                        Logger.LogWarn("[API] - Namespace mod list empty, ignoring");
                    }
                    else if (nsMods.Count == 1)
                    {
                        builder.AppendLine($"{nsMods[0].GetName()} : {ModVersionsCache[nsMods[0].GetName()]}");
                    }
                    else
                    {
                        builder.Append($"{ns} : ");
                        for (int i = 0; i < nsMods.Count; i++)
                        {
                            builder.Append(nsMods[i].GetName() + (i == nsMods.Count - 1 ? Environment.NewLine : ", "));
                            if ((i + 1) % 4 == 0 && i < nsMods.Count - 1)
                            {
                                builder.Append(Environment.NewLine + "\t");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError($"[API] - Failed to append mod name text:\n{e}");
                }
            }

            _draw.drawString = builder.ToString();
        }

        internal static void LoadMod(IMod mod, bool updateModText, bool changeSettings = true, Dictionary<string, Dictionary<string, GameObject>> preloadedObjects = null)
        {
            if(changeSettings || !ModHooks.Instance.GlobalSettings.ModEnabledSettings.ContainsKey(mod.GetName()))
                ModHooks.Instance.GlobalSettings.ModEnabledSettings[mod.GetName()] = true;

            mod.Initialize(preloadedObjects);

            if (!ModHooks.Instance.LoadedModsWithVersions.ContainsKey(mod.GetType().Name))
                ModHooks.Instance.LoadedModsWithVersions.Add(mod.GetType().Name, mod.GetVersion());
            else
                ModHooks.Instance.LoadedModsWithVersions[mod.GetType().Name] = mod.GetVersion();

            if (ModHooks.Instance.LoadedMods.All(x => x != mod.GetType().Name))
                ModHooks.Instance.LoadedMods.Add(mod.GetType().Name);

            if (updateModText)
                UpdateModText();
        }

        internal static void UnloadMod(ITogglableMod mod) 
        {
            try
            {
                ModHooks.Instance.GlobalSettings.ModEnabledSettings[mod.GetName()] = false;
                ModHooks.Instance.LoadedModsWithVersions.Remove(mod.GetType().Name);
                ModHooks.Instance.LoadedMods.Remove(mod.GetType().Name);

                mod.Unload();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[API] - Failed to unload Mod - {mod.GetName()} - {Environment.NewLine} - {ex} ");
            }

            UpdateModText();
        }

        /// <summary>
        /// Checks to see if a class is a subclass of a generic class.
        /// </summary>
        /// <param name="generic">Generic to compare against.</param>
        /// <param name="toCheck">Type to check</param>
        /// <returns></returns>
        private static bool IsSubclassOfRawGeneric(Type generic, Type toCheck)
        {
            while (toCheck != null && toCheck != typeof(object))
            {
                Type type = toCheck.IsGenericType ? toCheck.GetGenericTypeDefinition() : toCheck;
                if (generic == type)
                {
                    return true;
                }
                toCheck = toCheck.BaseType;
            }
            return false;
        }
        /// <summary>
        /// Checks if the mod loads are done.
        /// </summary>
        public static bool Loaded;

        /// <summary>
        /// Is Debug Enabled
        /// </summary>
        public static bool Debug;

        /// <summary>
        /// List of loaded mods.
        /// </summary>
        public static List<IMod> LoadedMods = new List<IMod>();

        private static readonly Dictionary<string, string> ModVersionsCache = new Dictionary<string, string>();
        
    }
}
