﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GlobalEnums;
using JetBrains.Annotations;
using Modding.Menu;
using MonoMod;
using UnityEngine;

// ReSharper disable SuggestVarOrType_SimpleTypes
#pragma warning disable 1591
namespace Modding
{
    /// <summary>
    /// Class to hook into various events for the game.
    /// </summary>
    [PublicAPI]
    public class ModHooks
    {
        internal static bool IsInitialized;

        private const int _modVersion = 48;

        /// <summary>
        /// Contains the seperator for path's, useful for handling Mac vs Windows vs Linux
        /// </summary>
        public static readonly char PathSeperator = SystemInfo.operatingSystem.Contains("Windows") ? '\\' : '/';

        private static readonly string SettingsPath = Application.persistentDataPath + PathSeperator + "ModdingApi.GlobalSettings.json";
        private static ModHooks _instance;

        private ModHooksGlobalSettings _globalSettings;

        internal ModHooksGlobalSettings GlobalSettings
        {
            get
            {
                if (_globalSettings != null) return _globalSettings;

                LoadGlobalSettings();

                if (_globalSettings.ModEnabledSettings == null) _globalSettings.ModEnabledSettings = new SerializableBoolDictionary();

                return _globalSettings;
            }
        }

        /// <summary>
        /// Currently Loaded Mods
        /// </summary>
        public readonly List<string> LoadedMods = new List<string>();

        /// <summary>
        /// Dictionary of mods and their version #s
        /// </summary>
        public readonly SerializableStringDictionary LoadedModsWithVersions = new SerializableStringDictionary();

        /// <summary>
        /// The Version of the Modding API
        /// </summary>
        public string ModVersion;

        /// <summary>
        /// Version of the Game
        /// </summary>
        public GameVersionData version;

        /// <summary>
        /// Denotes if the API is current
        /// </summary>
        // ReSharper disable once ConvertToConstant.Global
        public readonly bool IsCurrent = true;

        private Console _console;

        internal void LogConsole(string message)
        {
            try
            {
                if (!GlobalSettings.ShowDebugLogInGame) return;
                
                if (_console == null)
                {
                    GameObject go = new GameObject();
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _console = go.AddComponent<Console>();
                }

                _console.AddText(message);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private ModHooks()
        {
            Logger.Log("[API] - Adding GitHub SSL Cert to Allow for Checking of Mod Versions");

            SetupServicePointAuthorizor();
            ModManager _ = new ModManager();
            Logger.SetLogLevel(GlobalSettings.LoggingLevel);
            GameVersion gameVersion;

            try
            {
                string[] versionNums = Constants.GAME_VERSION.Split('.');

                gameVersion.major = Convert.ToInt32(versionNums[0]);
                gameVersion.minor = Convert.ToInt32(versionNums[1]);
                gameVersion.revision = Convert.ToInt32(versionNums[2]);
                gameVersion.package = Convert.ToInt32(versionNums[3]);
            }
            catch (Exception e)
            {
                gameVersion.major = 0;
                gameVersion.minor = 0;
                gameVersion.revision = 0;
                gameVersion.package = 0;

                Logger.LogError("[API] - Failed obtaining game version:\n" + e);
            }

            // ReSharper disable once Unity.IncorrectScriptableObjectInstantiation idk it works
            version = new GameVersionData {gameVersion = gameVersion};

            ModVersion = version.GetGameVersionString() + "-" + _modVersion;

            ApplicationQuitHook += SaveGlobalSettings;

            // Wyza - Have to disable this.  Unity doesn't support TLS 1.2 and github removed TLS 1.0/1.1 support.  Grumble
            // try
            // {
            //     GithubVersionHelper githubVersionHelper = new GithubVersionHelper("seanpr96/HollowKnight.Modding");

            //     string currentGithubVersion = githubVersionHelper.GetVersion();
            //     string[] temp = currentGithubVersion.Split('-');
            //     int modVersionRevision = Convert.ToInt32(temp[1]);
            //     Version tempNewVersion = new Version(temp[0]);
            //     Version tempGameVersion = new Version(gameVersion.major, gameVersion.minor, gameVersion.revision, gameVersion.package);
            //     Logger.LogDebug("[API] - Checking Game Version: " + tempGameVersion + " < " + tempNewVersion);
            //     if(tempNewVersion.CompareTo(tempGameVersion ) < 0 || (tempNewVersion.CompareTo(tempGameVersion) == 0 && modVersionRevision > _modVersion))
            //         IsCurrent = false;
            // }
            // catch(Exception ex)
            // {
            //     Logger.LogError("[API] - Couldn't check for new version." + ex);
            // }

            IsInitialized = true;
        }

        // Used to make the Github Certificate valid so that we can check for new versions.
        // Used this command in linux: openssl s_client -connect api.github.com:443
        private static void SetupServicePointAuthorizor()
        {
            X509Certificate2 gitHubCertificate = new X509Certificate2();
            gitHubCertificate.Import(GetEmbeddedCertBytes("github.crt"));

            X509Certificate2 gitHubCertificate2 = new X509Certificate2();
            gitHubCertificate2.Import(GetEmbeddedCertBytes("github2.crt"));

            ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, errors) =>
            {
                Logger.LogDebug(errors.ToString());
                Logger.Log(certificate.Subject);
                return certificate.Equals(gitHubCertificate) || certificate.Equals(gitHubCertificate2);
            };
        }

        private static byte[] GetEmbeddedCertBytes(string name)
        {
            string resourceName = "Modding." + name;

            using (Stream stream = Assembly.GetCallingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    return null;
                using (StreamReader reader = new StreamReader(stream))
                {
                    string result = reader.ReadToEnd();
                    return Encoding.ASCII.GetBytes(result);
                }
            }
        }

        /// <summary>
        /// Current instance of Modhooks.
        /// </summary>
        public static ModHooks Instance
        {
            get
            {
                if (_instance != null) return _instance;

                _instance = new ModHooks();
                return _instance;
            }
        }

        /// <summary>
        /// Logs the message to ModLog.txt in the save file path.
        /// </summary>
        /// <param name="info">Message To Log</param>
        [Obsolete("This method is obsolete and will be removed in future Mod API Versions. Use Logger instead for global calls and just Log for mod calls..")]
        public static void ModLog(string info)
        {
            Logger.Log(info);
        }

        #region PlayerManagementHandling

        /// <summary>
        /// Called when anything in the game tries to set a bool in player data
        /// </summary>
        /// <remarks>PlayerData.SetBool</remarks>
        /// <see cref="SetBoolProxy"/>
        [HookInfo("Called when anything in the game tries to set a bool in player data", "PlayerData.SetBool")]
        public event SetBoolProxy SetPlayerBoolHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SetPlayerBoolHook");
                _SetPlayerBoolHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SetPlayerBoolHook");
                _SetPlayerBoolHook -= value;
            }
        }

        private event SetBoolProxy _SetPlayerBoolHook;

        /// <summary>
        /// Called by the game in PlayerData.SetBool 
        /// </summary>
        /// <param name="target">Target Field Name</param>
        /// <param name="val">Value to set</param>
        internal void SetPlayerBool(string target, bool val)
        {
            if (_SetPlayerBoolHook != null)
            {
                SetBoolProxy[] invocationList = (SetBoolProxy[]) _SetPlayerBoolHook.GetInvocationList();
                
                foreach (SetBoolProxy toInvoke in invocationList)
                {
                    try
                    {
                        toInvoke.Invoke(target, val);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("[API] - " + ex);
                    }
                }

                return;
            }

            Patches.PlayerData.instance.SetBoolInternal(target, val);
        }


        /// <summary>
        /// Called when anything in the game tries to get a bool from player data
        /// </summary>
        /// <remarks>PlayerData.GetBool</remarks>
        [HookInfo("Called when anything in the game tries to get a bool from player data", "PlayerData.GetBool")]
        public event GetBoolProxy GetPlayerBoolHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding GetPlayerBoolHook");
                _GetPlayerBoolHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing GetPlayerBoolHook");
                _GetPlayerBoolHook -= value;
            }
        }

        private event GetBoolProxy _GetPlayerBoolHook;

        /// <summary>
        /// Called by the game in PlayerData.GetBool
        /// </summary>
        /// <param name="target">Target Field Name</param>
        internal bool GetPlayerBool(string target)
        {
            bool boolInternal = Patches.PlayerData.instance.GetBoolInternal(target);
            bool result = boolInternal;
            bool gotValue = false;
            if (_GetPlayerBoolHook == null) return result;

            GetBoolProxy[] invocationList = (GetBoolProxy[]) _GetPlayerBoolHook.GetInvocationList();
            
            foreach (var toInvoke in invocationList)
            {
                try
                {
                    bool flag2 = toInvoke.Invoke(target);
                    
                    if (flag2 == boolInternal || gotValue) continue;

                    result = flag2;
                    gotValue = true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }

        /// <summary>
        /// Called when anything in the game tries to set an int in player data
        /// </summary>
        /// <remarks>PlayerData.SetInt</remarks>
        [HookInfo("Called when anything in the game tries to set an int in player data", "PlayerData.SetInt")]
        public event SetIntProxy SetPlayerIntHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SetPlayerIntHook");
                _SetPlayerIntHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SetPlayerIntHook");
                _SetPlayerIntHook -= value;
            }
        }

        private event SetIntProxy _SetPlayerIntHook;

        /// <summary>
        /// Called by the game in PlayerData.SetInt 
        /// </summary>
        /// <param name="target">Target Field Name</param>
        /// <param name="val">Value to set</param>
        internal void SetPlayerInt(string target, int val)
        {
            if (_SetPlayerIntHook != null)
            {
                SetIntProxy[] invocationList = (SetIntProxy[]) _SetPlayerIntHook.GetInvocationList();
                
                foreach (SetIntProxy toInvoke in invocationList)
                {
                    try
                    {
                        toInvoke.Invoke(target, val);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("[API] - " + ex);
                    }
                }

                return;
            }

            Patches.PlayerData.instance.SetIntInternal(target, val);
        }

        /// <summary>
        /// Called when anything in the game tries to get an int from player data
        /// </summary>
        /// <remarks>PlayerData.GetInt</remarks>
        [HookInfo("Called when anything in the game tries to get an int from player data", "PlayerData.GetInt")]
        public event GetIntProxy GetPlayerIntHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding GetPlayerIntHook");
                _GetPlayerIntHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing GetPlayerIntHook");
                _GetPlayerIntHook -= value;
            }
        }

        private event GetIntProxy _GetPlayerIntHook;

        /// <summary>
        /// Called by the game in PlayerData.GetInt 
        /// </summary>
        /// <param name="target">Target Field Name</param>
        internal int GetPlayerInt(string target)
        {
            int intInternal = Patches.PlayerData.instance.GetIntInternal(target);
            int result = intInternal;
            bool gotValue = false;
            
            if (_GetPlayerIntHook == null) return result;

            GetIntProxy[] invocationList = (GetIntProxy[]) _GetPlayerIntHook.GetInvocationList();
            
            foreach (GetIntProxy toInvoke in invocationList)
            {
                try
                {
                    int num = toInvoke.Invoke(target);
                    if (num == intInternal || gotValue) continue;

                    result = num;
                    gotValue = true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }

        /// <summary>
        /// Called when anything in the game tries to set a float in player data
        /// </summary>
        /// <remarks>PlayerData.SetFloat</remarks>
        [HookInfo("Called when anything in the game tries to set a float in player data", "PlayerData.SetFloat")]
        public event SetFloatProxy SetPlayerFloatHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SetPlayerFloatHook");
                _SetPlayerFloatHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SetPlayerFloatHook");
                _SetPlayerFloatHook -= value;
            }
        }

        private event SetFloatProxy _SetPlayerFloatHook;

        /// <summary>
        /// Called by the game in PlayerData.SetFloat 
        /// </summary>
        /// <param name="target">Target Field Name</param>
        /// <param name="val">Value to set</param>
        internal void SetPlayerFloat(string target, float val)
        {
            if (_SetPlayerFloatHook != null)
            {
                SetFloatProxy[] invocationList = (SetFloatProxy[]) _SetPlayerFloatHook.GetInvocationList();
                
                foreach (SetFloatProxy toInvoke in invocationList)
                {
                    try
                    {
                        toInvoke.Invoke(target, val);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("[API] - " + ex);
                    }
                }

                return;
            }

            Patches.PlayerData.instance.SetFloatInternal(target, val);
        }

        /// <summary>
        /// Called when anything in the game tries to get a float from player data
        /// </summary>
        /// <remarks>PlayerData.GetFloat</remarks>
        [HookInfo("Called when anything in the game tries to get a float from player data", "PlayerData.GetFloat")]
        public event GetFloatProxy GetPlayerFloatHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding GetPlayerFloatHook");
                _GetPlayerFloatHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing GetPlayerFloatHook");
                _GetPlayerFloatHook -= value;
            }
        }

        private event GetFloatProxy _GetPlayerFloatHook;

        /// <summary>
        /// Called by the game in PlayerData.GetFloat 
        /// </summary>
        /// <param name="target">Target Field Name</param>
        internal float GetPlayerFloat(string target)
        {
            float floatInternal = Patches.PlayerData.instance.GetFloatInternal(target);
            float result = floatInternal;
            bool gotValue = false;
            
            if (_GetPlayerFloatHook == null) return result;

            GetFloatProxy[] invocationList = (GetFloatProxy[]) _GetPlayerFloatHook.GetInvocationList();
            
            foreach (GetFloatProxy toInvoke in invocationList)
            {
                try
                {
                    float f = toInvoke.Invoke(target);
                    
                    // ReSharper disable once CompareOfFloatsByEqualityOperator 
                    if (f == floatInternal || gotValue) continue;

                    result = f;
                    gotValue = true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }

        /// <summary>
        /// Called when anything in the game tries to set a string in player data
        /// </summary>
        /// <remarks>PlayerData.SetString</remarks>
        [HookInfo("Called when anything in the game tries to set a string in player data", "PlayerData.SetString")]
        public event SetStringProxy SetPlayerStringHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SetPlayerStringHook");
                _SetPlayerStringHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SetPlayerStringHook");
                _SetPlayerStringHook -= value;
            }
        }

        private event SetStringProxy _SetPlayerStringHook;

        /// <summary>
        /// Called by the game in PlayerData.SetString 
        /// </summary>
        /// <param name="target">Target Field Name</param>
        /// <param name="val">Value to set</param>
        internal void SetPlayerString(string target, string val)
        {
            if (_SetPlayerStringHook != null)
            {
                SetStringProxy[] invocationList = (SetStringProxy[]) _SetPlayerStringHook.GetInvocationList();
                
                foreach (SetStringProxy toInvoke in invocationList)
                {
                    try
                    {
                        toInvoke.Invoke(target, val);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("[API] - " + ex);
                    }
                }

                return;
            }

            Patches.PlayerData.instance.SetStringInternal(target, val);
        }

        /// <summary>
        /// Called when anything in the game tries to get a string from player data
        /// </summary>
        /// <remarks>PlayerData.GetString</remarks>
        [HookInfo("Called when anything in the game tries to get a string from player data", "PlayerData.GetString")]
        public event GetStringProxy GetPlayerStringHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding GetPlayerStringHook");
                _GetPlayerStringHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing GetPlayerStringHook");
                _GetPlayerStringHook -= value;
            }
        }

        private event GetStringProxy _GetPlayerStringHook;

        /// <summary>
        /// Called by the game in PlayerData.GetString 
        /// </summary>
        /// <param name="target">Target Field Name</param>
        internal string GetPlayerString(string target)
        {
            string stringInternal = Patches.PlayerData.instance.GetStringInternal(target);
            string result = stringInternal;
            bool gotValue = false;
            if (_GetPlayerStringHook == null) return result;

            GetStringProxy[] invocationList = (GetStringProxy[]) _GetPlayerStringHook.GetInvocationList();
            
            foreach (GetStringProxy toInvoke in invocationList)
            {
                try
                {
                    string s = toInvoke.Invoke(target);
                    if (s == stringInternal || gotValue) continue;

                    result = s;
                    gotValue = true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }

        /// <summary>
        /// Called when anything in the game tries to set a Vector3 in player data
        /// </summary>
        /// <remarks>PlayerData.SetVector3</remarks>
        [HookInfo("Called when anything in the game tries to set a Vector3 in player data", "PlayerData.SetVector3")]
        public event SetVector3Proxy SetPlayerVector3Hook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SetPlayerVector3Hook");
                _SetPlayerVector3Hook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SetPlayerVector3Hook");
                _SetPlayerVector3Hook -= value;
            }
        }

        private event SetVector3Proxy _SetPlayerVector3Hook;

        /// <summary>
        /// Called by the game in PlayerData.SetVector3 
        /// </summary>
        /// <param name="target">Target Field Name</param>
        /// <param name="val">Value to set</param>
        internal void SetPlayerVector3(string target, Vector3 val)
        {
            if (_SetPlayerVector3Hook != null)
            {
                SetVector3Proxy[] invocationList = (SetVector3Proxy[]) _SetPlayerVector3Hook.GetInvocationList();
                
                foreach (SetVector3Proxy toInvoke in invocationList)
                {
                    try
                    {
                        toInvoke.Invoke(target, val);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("[API] - " + ex);
                    }
                }

                return;
            }

            Patches.PlayerData.instance.SetVector3Internal(target, val);
        }

        /// <summary>
        /// Called when anything in the game tries to get a Vector3 from player data
        /// </summary>
        /// <remarks>PlayerData.GetVector3</remarks>
        [HookInfo("Called when anything in the game tries to get a Vector3 from player data", "PlayerData.GetVector3")]
        public event GetVector3Proxy GetPlayerVector3Hook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding GetPlayerVector3Hook");
                _GetPlayerVector3Hook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing GetPlayerVector3Hook");
                _GetPlayerVector3Hook -= value;
            }
        }

        private event GetVector3Proxy _GetPlayerVector3Hook;

        /// <summary>
        /// Called by the game in PlayerData.GetVector3
        /// </summary>
        /// <param name="target">Target Field Name</param>
        internal Vector3 GetPlayerVector3(string target)
        {
            Vector3 vecInternal = Patches.PlayerData.instance.GetVector3Internal(target);
            Vector3 result = vecInternal;
            bool gotValue = false;
            
            if (_GetPlayerVector3Hook == null) return result;

            GetVector3Proxy[] invocationList = (GetVector3Proxy[]) _GetPlayerVector3Hook.GetInvocationList();
            
            foreach (GetVector3Proxy toInvoke in invocationList)
            {
                try
                {
                    Vector3 vec = toInvoke.Invoke(target);
                    
                    if (vec == vecInternal || gotValue) continue;

                    result = vec;
                    gotValue = true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }

        /// <summary>
        /// Called when anything in the game tries to set a generic variable in player data
        /// </summary>
        /// <remarks>PlayerData.SetVariable</remarks>
        [HookInfo("Called when anything in the game tries to set a generic variable in player data", "PlayerData.SetVariable")]
        public event SetVariableProxy SetPlayerVariableHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SetPlayerVariableHook");
                _SetPlayerVariableHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SetPlayerVariableHook");
                _SetPlayerVariableHook -= value;
            }
        }

        private event SetVariableProxy _SetPlayerVariableHook;

        /// <summary>
        /// Called by the game in PlayerData.SetVariable 
        /// </summary>
        /// <param name="target">Target Field Name</param>
        /// <param name="val">Value to set</param>
        internal void SetPlayerVariable<T>(string target, T val)
        {
            Type t = typeof(T);
            
            if (t == typeof(bool))
            {
                SetPlayerBool(target, (bool) (object) val);
                return;
            }
            if (t == typeof(int))
            {
                SetPlayerInt(target, (int) (object) val);
                return;
            }
            if (t == typeof(float))
            {
                SetPlayerFloat(target, (float) (object) val);
                return;
            }
            if (t == typeof(string))
            {
                SetPlayerString(target, (string) (object) val);
                return;
            }
            if (t == typeof(Vector3))
            {
                SetPlayerVector3(target, (Vector3) (object) val);
                return;
            }

            if (_SetPlayerVariableHook != null)
            {
                SetVariableProxy[] invocationList = (SetVariableProxy[]) _SetPlayerVariableHook.GetInvocationList();
                
                foreach (SetVariableProxy toInvoke in invocationList)
                {
                    try
                    {
                        toInvoke.Invoke(typeof(T), target, val);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("[API] - " + ex);
                    }
                }

                return;
            }

            Patches.PlayerData.instance.SetVariableInternal(target, val);
        }

        /// <summary>
        /// Called when anything in the game tries to get a generic variable from player data
        /// </summary>
        /// <remarks>PlayerData.GetVariable</remarks>
        [HookInfo("Called when anything in the game tries to get a generic variable from player data", "PlayerData.GetVariable")]
        [PublicAPI]
        public event GetVariableProxy GetPlayerVariableHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding GetPlayerVariableHook");
                _GetPlayerVariableHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing GetPlayerVariableHook");
                _GetPlayerVariableHook -= value;
            }
        }

        private event GetVariableProxy _GetPlayerVariableHook;

        /// <summary>
        /// Called by the game in PlayerData.GetVariable
        /// </summary>
        /// <param name="target">Target Field Name</param>
        internal T GetPlayerVariable<T>(string target)
        {
            Type t = typeof(T);
            
            if (t == typeof(bool))
            {
                return (T) (object) GetPlayerBool(target);
            }
            if (t == typeof(int))
            {
                return (T) (object) GetPlayerInt(target);
            }
            if (t == typeof(float))
            {
                return (T) (object) GetPlayerFloat(target);
            }
            if (t == typeof(string))
            {
                return (T) (object) GetPlayerString(target);
            }
            if (t == typeof(Vector3))
            {
                return (T) (object) GetPlayerVector3(target);
            }

            T varInternal = Patches.PlayerData.instance.GetVariableInternal<T>(target);
            T result = varInternal;
            bool gotValue = false;
            
            if (_GetPlayerVariableHook == null) return result;

            GetVariableProxy[] invocationList = (GetVariableProxy[]) _GetPlayerVariableHook.GetInvocationList();
            
            foreach (GetVariableProxy toInvoke in invocationList)
            {
                try
                {
                    T v = (T) toInvoke.Invoke(typeof(T), target);
                    if (v.Equals(varInternal) || gotValue) continue;

                    result = v;
                    gotValue = true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }

        private event NewPlayerDataHandler _NewPlayerDataHook;

        /// <summary>
        /// Called after setting up a new PlayerData
        /// </summary>
        /// <remarks>PlayerData.SetupNewPlayerData</remarks>
        [HookInfo("Called after setting up a new PlayerData", "PlayerData.SetupNewPlayerData")]
        [Obsolete("Do Not Use - This is called too often due to a bug in the vanilla game's FSM handling.", true)]
        public event NewPlayerDataHandler NewPlayerDataHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding NewPlayerDataHook");
                _NewPlayerDataHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing NewPlayerDataHook");
                _NewPlayerDataHook -= value;
            }
        }

        /// <summary>
        /// Called after setting up a new PlayerData.SetupNewPlayerData
        /// </summary>
        internal void AfterNewPlayerData(PlayerData instance)
        {
            Logger.LogFine("[API] - AfterNewPlayerData Invoked");

            if (_NewPlayerDataHook == null) return;
            
            NewPlayerDataHandler[] invocationList = (NewPlayerDataHandler[]) _NewPlayerDataHook.GetInvocationList();
            
            foreach (NewPlayerDataHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(instance);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called whenever blue health is updated
        /// </summary>
        [HookInfo("Called whenever blue health is updated", "PlayerData.UpdateBlueHealth")]
        public event BlueHealthHandler BlueHealthHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding BlueHealthHook");
                _BlueHealthHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing BlueHealthHook");
                _BlueHealthHook -= value;
            }
        }

        private event BlueHealthHandler _BlueHealthHook;

        /// <summary>
        /// Called whenever blue health is updated
        /// </summary>
        internal int OnBlueHealth()
        {
            Logger.LogFine("[API] - OnBlueHealth Invoked");

            int result = 0;
            if (_BlueHealthHook == null) return result;

            BlueHealthHandler[] invocationList = (BlueHealthHandler[]) _BlueHealthHook.GetInvocationList();
            
            foreach (BlueHealthHandler toInvoke in invocationList)
            {
                try
                {
                    result = toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }


        /// <summary>
        /// Called when health is taken from the player
        /// </summary>
        /// <remarks>HeroController.TakeHealth</remarks>
        [HookInfo("Called when health is taken from the player", "PlayerData.TakeHealth")]
        public event TakeHealthProxy TakeHealthHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding TakeHealthHook");
                _TakeHealthHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing TakeHealthHook");
                _TakeHealthHook -= value;
            }
        }

        private event TakeHealthProxy _TakeHealthHook;

        /// <summary>
        /// Called when health is taken from the player
        /// </summary>
        /// <remarks>HeroController.TakeHealth</remarks>
        internal int OnTakeHealth(int damage)
        {
            Logger.LogFine("[API] - OnTakeHealth Invoked");

            if (_TakeHealthHook == null) return damage;

            TakeHealthProxy[] invocationList = (TakeHealthProxy[]) _TakeHealthHook.GetInvocationList();
            
            foreach (TakeHealthProxy toInvoke in invocationList)
            {
                try
                {
                    damage = toInvoke.Invoke(damage);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return damage;
        }

        /// <summary>
        /// Called when damage is dealt to the player
        /// </summary>
        /// <remarks>HeroController.TakeDamage</remarks>
        [HookInfo("Called when damage is dealt to the player", "HeroController.TakeDamage")]
        public event TakeDamageProxy TakeDamageHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding TakeDamageHook");
                _TakeDamageHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing TakeDamageHook");
                _TakeDamageHook -= value;
            }
        }

        private event TakeDamageProxy _TakeDamageHook;

        /// <summary>
        /// Called when damage is dealt to the player
        /// </summary>
        /// <remarks>HeroController.TakeDamage</remarks>
        internal int OnTakeDamage(ref int hazardType, int damage)
        {
            Logger.LogFine("[API] - OnTakeDamage Invoked");

            if (_TakeDamageHook == null) return damage;

            TakeDamageProxy[] invocationList = (TakeDamageProxy[]) _TakeDamageHook.GetInvocationList();
            
            foreach (TakeDamageProxy toInvoke in invocationList)
            {
                try
                {
                    damage = toInvoke.Invoke(ref hazardType, damage);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return damage;
        }

        /// <summary>
        /// Called at the end of the take damage function
        /// </summary>
        [HookInfo("Called at the end of the take damage function", "HeroController.TakeDamage")]
        public event AfterTakeDamageHandler AfterTakeDamageHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding AfterTakeDamageHook");
                _AfterTakeDamageHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing AfterTakeDamageHook");
                _AfterTakeDamageHook -= value;
            }
        }

        private event AfterTakeDamageHandler _AfterTakeDamageHook;

        /// <summary>
        /// Called at the end of the take damage function
        /// </summary>
        internal int AfterTakeDamage(int hazardType, int damageAmount)
        {
            Logger.LogFine("[API] - AfterTakeDamage Invoked");

            if (_AfterTakeDamageHook == null) return damageAmount;

            AfterTakeDamageHandler[] invocationList = (AfterTakeDamageHandler[]) _AfterTakeDamageHook.GetInvocationList();
            
            foreach (AfterTakeDamageHandler toInvoke in invocationList)
            {
                try
                {
                    damageAmount = toInvoke.Invoke(hazardType, damageAmount);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return damageAmount;
        }

        /// <summary>
        /// Called when the player dies
        /// </summary>
        /// <remarks>GameManager.PlayerDead</remarks>
        [HookInfo("Called when the player dies", "GameManager.PlayerDead")]
        public event VoidHandler BeforePlayerDeadHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding BeforePlayerDeadHook");
                _BeforePlayerDeadHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing BeforePlayerDeadHook");
                _BeforePlayerDeadHook -= value;
            }
        }

        private event VoidHandler _BeforePlayerDeadHook;

        /// <summary>
        /// Called when the player dies (at the beginning of the method)
        /// </summary>
        /// <remarks>GameManager.PlayerDead</remarks>
        internal void OnBeforePlayerDead()
        {
            Logger.LogFine("[API] - OnBeforePlayerDead Invoked");

            if (_BeforePlayerDeadHook == null) return;

            VoidHandler[] invocationList = (VoidHandler[]) _BeforePlayerDeadHook.GetInvocationList();
            
            foreach (VoidHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called after the player dies
        /// </summary>
        /// <remarks>GameManager.PlayerDead</remarks>
        [HookInfo("Called after the player dies", "GameManager.PlayerDead")]
        public event VoidHandler AfterPlayerDeadHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding AfterPlayerDeadHook");
                _AfterPlayerDeadHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing AfterPlayerDeadHook");
                _AfterPlayerDeadHook -= value;
            }
        }

        private event VoidHandler _AfterPlayerDeadHook;

        /// <summary>
        /// Called after the player dies (at the end of the method)
        /// </summary>
        /// <remarks>GameManager.PlayerDead</remarks>
        internal void OnAfterPlayerDead()
        {
            Logger.LogFine("[API] - OnAfterPlayerDead Invoked");

            if (_AfterPlayerDeadHook == null) return;

            VoidHandler[] invocationList = (VoidHandler[]) _AfterPlayerDeadHook.GetInvocationList();
            
            foreach (VoidHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called whenever the player attacks
        /// </summary>
        /// <remarks>HeroController.Attack</remarks>
        [HookInfo("Called whenever the player attacks", "HeroController.Attack")]
        public event AttackHandler AttackHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding AttackHook");
                _AttackHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing AttackHook");
                _AttackHook -= value;
            }
        }

        private event AttackHandler _AttackHook;

        /// <summary>
        /// Called whenever the player attacks
        /// </summary>
        /// <remarks>HeroController.Attack</remarks>
        internal void OnAttack(AttackDirection dir)
        {
            Logger.LogFine("[API] - OnAttack Invoked");

            if (_AttackHook == null) return;
            
            AttackHandler[] invocationList = (AttackHandler[]) _AttackHook.GetInvocationList();
            
            foreach (AttackHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(dir);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called at the start of the DoAttack function
        /// </summary>
        [HookInfo("Called at the start of the DoAttack function", "HeroController.DoAttack")]
        public event DoAttackHandler DoAttackHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding DoAttackHook");
                _DoAttackHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing DoAttackHook");
                _DoAttackHook -= value;
            }
        }

        private event DoAttackHandler _DoAttackHook;


        /// <summary>
        /// Called at the start of the DoAttack function
        /// </summary>
        internal void OnDoAttack()
        {
            Logger.LogFine("[API] - OnDoAttack Invoked");

            if (_DoAttackHook == null) return;
            
            DoAttackHandler[] invocationList = (DoAttackHandler[]) _DoAttackHook.GetInvocationList();
            
            foreach (DoAttackHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }


        /// <summary>
        /// Called at the end of the attack function
        /// </summary>
        /// <remarks>HeroController.Attack</remarks>
        [HookInfo("Called at the end of the attack function", "HeroController.Attack")]
        [MonoModPublic]
        public event AfterAttackHandler AfterAttackHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding AfterAttackHook");
                _AfterAttackHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing AfterAttackHook");
                _AfterAttackHook -= value;
            }
        }

        private event AfterAttackHandler _AfterAttackHook;

        /// <summary>
        /// Called at the end of the attack function
        /// </summary>
        /// <remarks>HeroController.Attack</remarks>
        internal void AfterAttack(AttackDirection dir)
        {
            Logger.LogFine("[API] - AfterAttack Invoked");

            if (_AfterAttackHook == null) return;
            
            AfterAttackHandler[] invocationList = (AfterAttackHandler[]) _AfterAttackHook.GetInvocationList();
            
            foreach (AfterAttackHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(dir);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called whenever nail strikes something
        /// </summary>
        [HookInfo("Called whenever nail strikes something", "NailSlash.OnTriggerEnter2D")]
        public event SlashHitHandler SlashHitHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SlashHitHook");
                _SlashHitHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SlashHitHook");
                _SlashHitHook -= value;
            }
        }

        private event SlashHitHandler _SlashHitHook;

        /// <summary>
        /// Called whenever nail strikes something
        /// </summary>
        internal void OnSlashHit(Collider2D otherCollider, GameObject gameObject)
        {
            Logger.LogFine("[API] - OnSlashHit Invoked");

            if (otherCollider == null) return;

            if (_SlashHitHook == null) return;
            
            SlashHitHandler[] invocationList = (SlashHitHandler[]) _SlashHitHook.GetInvocationList();
            
            foreach (SlashHitHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(otherCollider, gameObject);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called after player values for charms have been set
        /// </summary>
        /// <remarks>HeroController.CharmUpdate</remarks>
        [HookInfo("Called after player values for charms have been set", "HeroController.CharmUpdate")]
        public event CharmUpdateHandler CharmUpdateHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding CharmUpdateHook");
                _CharmUpdateHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing CharmUpdateHook");
                _CharmUpdateHook -= value;
            }
        }

        private event CharmUpdateHandler _CharmUpdateHook;


        /// <summary>
        /// Called after player values for charms have been set
        /// </summary>
        /// <remarks>HeroController.CharmUpdate</remarks>
        internal void OnCharmUpdate()
        {
            Logger.LogFine("[API] - OnCharmUpdate Invoked");

            if (_CharmUpdateHook == null) return;
            
            CharmUpdateHandler[] invocationList = (CharmUpdateHandler[]) _CharmUpdateHook.GetInvocationList();
            
            foreach (CharmUpdateHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(PlayerData.instance, HeroController.instance);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called whenever the hero updates
        /// </summary>
        /// <remarks>HeroController.Update</remarks>
        [HookInfo("Called whenever the hero updates", "HeroController.Update")]
        public event HeroUpdateHandler HeroUpdateHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding HeroUpdateHook");
                _HeroUpdateHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing HeroUpdateHook");
                _HeroUpdateHook -= value;
            }
        }

        private event HeroUpdateHandler _HeroUpdateHook;

        /// <summary>
        /// Called whenever the hero updates
        /// </summary>
        /// <remarks>HeroController.Update</remarks>
        internal void OnHeroUpdate()
        {
            //Logger.LogFine("[API] - OnHeroUpdate Invoked");

            if (_HeroUpdateHook == null) return;
            
            HeroUpdateHandler[] invocationList = (HeroUpdateHandler[]) _HeroUpdateHook.GetInvocationList();
            
            foreach (HeroUpdateHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called whenever the player heals
        /// </summary>
        /// <remarks>PlayerData.health</remarks>
        public event BeforeAddHealthHandler BeforeAddHealthHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding BeforeAddHealthHook");
                _BeforeAddHealthHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing BeforeAddHealthHook");
                _BeforeAddHealthHook -= value;
            }
        }

        public event BeforeAddHealthHandler _BeforeAddHealthHook;

        /// <summary>
        /// Called whenever the player heals
        /// </summary>
        /// <remarks>PlayerData.health</remarks>
        internal int BeforeAddHealth(int amount)
        {
            Logger.LogFine("[API] - BeforeAddHealth Invoked");

            if (_BeforeAddHealthHook == null) return amount;

            BeforeAddHealthHandler[] invocationList = (BeforeAddHealthHandler[]) _BeforeAddHealthHook.GetInvocationList();
            
            foreach (BeforeAddHealthHandler toInvoke in invocationList)
            {
                try
                {
                    amount = toInvoke.Invoke(amount);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return amount;
        }

        /// <summary>
        /// Called whenever focus cost is calculated
        /// </summary>
        [HookInfo("Called whenever focus cost is calculated", "HeroController.StartMPDrain")]
        public event FocusCostHandler FocusCostHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding FocusCostHook");
                _FocusCostHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing FocusCostHook");
                _FocusCostHook -= value;
            }
        }

        private event FocusCostHandler _FocusCostHook;

        /// <summary>
        /// Called whenever focus cost is calculated
        /// </summary>
        internal float OnFocusCost()
        {
            Logger.LogFine("[API] - OnFocusCost Invoked");

            float result = 1f;
            
            if (_FocusCostHook == null) return result;

            FocusCostHandler[] invocationList = (FocusCostHandler[]) _FocusCostHook.GetInvocationList();
            
            foreach (FocusCostHandler toInvoke in invocationList)
            {
                try
                {
                    result = toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }

        /// <summary>
        /// Called when Hero recovers Soul from hitting enemies
        /// </summary>
        [HookInfo("Called when Hero recovers Soul from hitting enemies", "HeroController.SoulGain")]
        public event SoulGainHandler SoulGainHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SoulGainHook");
                _SoulGainHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SoulGainHook");
                _SoulGainHook -= value;
            }
        }

        private event SoulGainHandler _SoulGainHook;


        /// <summary>
        /// Called when Hero recovers Soul from hitting enemies
        /// </summary>
        internal int OnSoulGain(int num)
        {
            Logger.LogFine("[API] - OnSoulGain Invoked");

            if (_SoulGainHook == null) return num;

            SoulGainHandler[] invocationList = (SoulGainHandler[]) _SoulGainHook.GetInvocationList();
            
            foreach (SoulGainHandler toInvoke in invocationList)
            {
                try
                {
                    num = toInvoke.Invoke(num);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return num;
        }


        /// <summary>
        /// Called during dash function to change velocity
        /// </summary>
        /// <remarks>HeroController.Dash</remarks>
        [HookInfo("Called during dash function to change velocity", "HeroController.Dash")]
        public event DashVelocityHandler DashVectorHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding DashVectorHook");
                _DashVectorHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing DashVectorHook");
                _DashVectorHook -= value;
            }
        }

        private event DashVelocityHandler _DashVectorHook;

        /// <summary>
        /// Called during dash function to change velocity
        /// </summary>
        /// <remarks>HeroController.Dash</remarks>
        internal Vector2 DashVelocityChange(Vector2 change)
        {
            Logger.LogFine("[API] - DashVelocityChange Invoked");

            if (_DashVectorHook == null) return change;

            DashVelocityHandler[] invocationList = (DashVelocityHandler[]) _DashVectorHook.GetInvocationList();
            
            foreach (DashVelocityHandler toInvoke in invocationList)
            {
                try
                {
                    change = toInvoke.Invoke(change);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return change;
        }

        /// <summary>
        /// Called whenever the dash key is pressed. Returns whether or not to override normal dash functionality
        /// </summary>
        /// <remarks>HeroController.LookForQueueInput</remarks>
        [HookInfo("Called whenever the dash key is pressed. Returns whether or not to override normal dash functionality", "HeroController.LookForQueueInput")]
        public event DashPressedHandler DashPressedHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding DashPressedHook");
                _DashPressedHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing DashPressedHook");
                _DashPressedHook -= value;
            }
        }

        private event DashPressedHandler _DashPressedHook;

        /// <summary>
        /// Called whenever the dash key is pressed. Returns whether or not to override normal dash functionality
        /// </summary>
        /// <remarks>HeroController.LookForQueueInput</remarks>
        internal bool OnDashPressed()
        {
            Logger.LogFine("[API] - OnDashPressed Invoked");

            if (_DashPressedHook == null) return false;

            bool ret = false;

            DashPressedHandler[] invocationList = (DashPressedHandler[]) _DashPressedHook.GetInvocationList();
            
            foreach (DashPressedHandler toInvoke in invocationList)
            {
                try
                {
                    ret |= toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return ret;
        }

        #endregion


        #region SaveHandling

        /// <summary>
        /// Called directly after a save has been loaded
        /// </summary>
        /// <remarks>GameManager.LoadGame</remarks>
        [HookInfo("Called directly after a save has been loaded", "GameManager.LoadGame")]
        public event SavegameLoadHandler SavegameLoadHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SavegameLoadHook");
                _SavegameLoadHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SavegameLoadHook");
                _SavegameLoadHook -= value;
            }
        }

        private event SavegameLoadHandler _SavegameLoadHook;


        /// <summary>
        /// Called directly after a save has been loaded
        /// </summary>
        /// <remarks>GameManager.LoadGame</remarks>
        internal void OnSavegameLoad(int id)
        {
            Logger.LogFine("[API] - OnSavegameLoad Invoked");

            if (_SavegameLoadHook == null) return;
            
            SavegameLoadHandler[] invocationList = (SavegameLoadHandler[]) _SavegameLoadHook.GetInvocationList();
            
            foreach (SavegameLoadHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(id);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called directly after a save has been saved
        /// </summary>
        /// <remarks>GameManager.SaveGame</remarks>
        [HookInfo("Called directly after a save has been saved", "GameManager.SaveGame")]
        public event SavegameSaveHandler SavegameSaveHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SavegameSaveHook");
                _SavegameSaveHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SavegameSaveHook");
                _SavegameSaveHook -= value;
            }
        }

        private event SavegameSaveHandler _SavegameSaveHook;

        /// <summary>
        /// Called directly after a save has been saved
        /// </summary>
        /// <remarks>GameManager.SaveGame</remarks>
        internal void OnSavegameSave(int id)
        {
            Logger.LogFine("[API] - OnSavegameSave Invoked");

            if (_SavegameSaveHook == null) return;
            
            SavegameSaveHandler[] invocationList = (SavegameSaveHandler[]) _SavegameSaveHook.GetInvocationList();
            
            foreach (SavegameSaveHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(id);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called whenever a new game is started
        /// </summary>
        /// <remarks>GameManager.LoadFirstScene</remarks>
        [HookInfo("Called whenever a new game is started", "GameManager.LoadFirstScene")]
        public event NewGameHandler NewGameHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding NewGameHook");
                _NewGameHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing NewGameHook");
                _NewGameHook -= value;
            }
        }

        private event NewGameHandler _NewGameHook;

        /// <summary>
        /// Called whenever a new game is started
        /// </summary>
        /// <remarks>GameManager.LoadFirstScene</remarks>
        internal void OnNewGame()
        {
            Logger.LogFine("[API] - OnNewGame Invoked");

            if (_NewGameHook == null) return;

            NewGameHandler[] invocationList = (NewGameHandler[]) _NewGameHook.GetInvocationList();
            
            foreach (NewGameHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called before a save file is deleted
        /// </summary>
        /// <remarks>GameManager.ClearSaveFile</remarks>
        [HookInfo("Called whenever a save file is deleted", "GameManager.ClearSaveFile")]
        public event ClearSaveGameHandler SavegameClearHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SavegameClearHook");
                _SavegameClearHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SavegameClearHook");
                _SavegameClearHook -= value;
            }
        }

        private event ClearSaveGameHandler _SavegameClearHook;

        /// <summary>
        /// Called before a save file is deleted
        /// </summary>
        /// <remarks>GameManager.ClearSaveFile</remarks>
        internal void OnSavegameClear(int id)
        {
            Logger.LogFine("[API] - OnSavegameClear Invoked");

            if (_SavegameClearHook == null) return;
            
            ClearSaveGameHandler[] invocationList = (ClearSaveGameHandler[]) _SavegameClearHook.GetInvocationList();
            
            foreach (ClearSaveGameHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(id);
                }

                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called directly after a save has been loaded.  Allows for accessing SaveGame instance.
        /// </summary>
        /// <remarks>GameManager.LoadGame</remarks>
        [HookInfo("Called directly after a save has been loaded.  Allows for accessing SaveGame instance.", "GameManager.LoadGame")]
        public event AfterSavegameLoadHandler AfterSavegameLoadHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding AfterSavegameLoadHook");
                _AfterSavegameLoadHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing AfterSavegameLoadHook");
                _AfterSavegameLoadHook -= value;
            }
        }

        private event AfterSavegameLoadHandler _AfterSavegameLoadHook;

        /// <summary>
        /// Called directly after a save has been loaded.  Allows for accessing SaveGame instance.
        /// </summary>
        /// <remarks>GameManager.LoadGame</remarks>
        internal void OnAfterSaveGameLoad(Patches.SaveGameData data)
        {
            Logger.LogFine("[API] - OnAfterSaveGameLoad Invoked");

            if (_AfterSavegameLoadHook == null) return;
            
            AfterSavegameLoadHandler[] invocationList = (AfterSavegameLoadHandler[]) _AfterSavegameLoadHook.GetInvocationList();
            
            foreach (AfterSavegameLoadHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(data);
                }

                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called directly before save has been saved to allow for changes to the data before persisted.
        /// </summary>
        /// <remarks>GameManager.SaveGame</remarks>
        [HookInfo("Called directly before save has been saved to allow for changes to the data before persisted.", "GameManager.SaveGame")]
        public event BeforeSavegameSaveHandler BeforeSavegameSaveHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding BeforeSavegameSaveHook");
                _BeforeSavegameSaveHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing BeforeSavegameSaveHook");
                _BeforeSavegameSaveHook -= value;
            }
        }

        private event BeforeSavegameSaveHandler _BeforeSavegameSaveHook;

        /// <summary>
        /// Called directly before save has been saved to allow for changes to the data before persisted.
        /// </summary>
        /// <remarks>GameManager.SaveGame</remarks>
        internal void OnBeforeSaveGameSave(Patches.SaveGameData data)
        {
            Logger.LogFine("[API] - OnBeforeSaveGameSave Invoked");
            data.LoadedMods = LoadedModsWithVersions;
            
            if (_BeforeSavegameSaveHook == null) return;
            
            BeforeSavegameSaveHandler[] invocationList = (BeforeSavegameSaveHandler[]) _BeforeSavegameSaveHook.GetInvocationList();
            
            foreach (BeforeSavegameSaveHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(data);
                }

                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Overrides the filename to load for a given slot.  Return null to use vanilla names.
        /// </summary>
        [HookInfo("Overrides the filename for a slot.", "GameManager.SaveGameClear")]
        public event GetSaveFileNameHandler GetSaveFileNameHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding GetSaveFileNameHook");
                _GetSaveFileNameHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing GetSaveFileNameHook");
                _GetSaveFileNameHook -= value;
            }
        }

        private event GetSaveFileNameHandler _GetSaveFileNameHook;

        /// <summary>
        /// Overrides the filename to load for a given slot.  Return null to use vanilla names.
        /// </summary>
        internal string GetSaveFileName(int saveSlot)
        {
            Logger.LogFine("[API] - GetSaveFileName Invoked");

            if (_GetSaveFileNameHook == null) return null;

            string ret = null;

            GetSaveFileNameHandler[] invocationList = (GetSaveFileNameHandler[]) _GetSaveFileNameHook.GetInvocationList();
            
            foreach (GetSaveFileNameHandler toInvoke in invocationList)
            {
                try
                {
                    ret = toInvoke.Invoke(saveSlot);
                }

                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return ret;
        }

        /// <summary>
        /// Called after a game has been cleared from a slot.
        /// </summary>
        [HookInfo("Called after a savegame has been cleared.", "GameManager.GetSaveFilename")]
        public event AfterClearSaveGameHandler AfterSaveGameClearHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding AfterSaveGameClearHook");
                _AfterSaveGameClearHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing AfterSaveGameClearHook");
                _AfterSaveGameClearHook -= value;
            }
        }

        private event AfterClearSaveGameHandler _AfterSaveGameClearHook;

        /// <summary>
        /// Called after a game has been cleared from a slot.
        /// </summary>
        internal void OnAfterSaveGameClear(int saveSlot)
        {
            Logger.LogFine("[API] - OnAfterSaveGameClear Invoked");

            if (_AfterSaveGameClearHook == null) return;
            
            AfterClearSaveGameHandler[] invocationList = (AfterClearSaveGameHandler[]) _AfterSaveGameClearHook.GetInvocationList();
            
            foreach (AfterClearSaveGameHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(saveSlot);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        #endregion

        /// <summary>
        /// Called whenever localization specific strings are requested
        /// </summary>
        /// <remarks>N/A</remarks>
        [HookInfo("Called whenever localization specific strings are requested", "N/A")]
        public event LanguageGetHandler LanguageGetHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding LanguageGetHook");
                _LanguageGetHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing LanguageGetHook");
                _LanguageGetHook -= value;
            }
        }

        private event LanguageGetHandler _LanguageGetHook;

        /// <summary>
        /// Called whenever localization specific strings are requested
        /// </summary>
        /// <remarks>N/A</remarks>
        internal string LanguageGet(string key, string sheet)
        {
            string @internal = Patches.Language.GetInternal(key, sheet);
            string result = @internal;
            bool gotText = false;
            
            if (_LanguageGetHook == null) return result;

            LanguageGetHandler[] invocationList = (LanguageGetHandler[]) _LanguageGetHook.GetInvocationList();
            
            foreach (LanguageGetHandler toInvoke in invocationList)
            {
                try
                {
                    string text = toInvoke.Invoke(key, sheet);
                    if (text == @internal || gotText) continue;

                    result = text;
                    gotText = true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }

        #region SceneHandling

        /// <summary>
        /// Called after a new Scene has been loaded
        /// </summary>
        /// <remarks>N/A</remarks>
        [HookInfo("Called after a new Scene has been loaded", "GameManager.LoadScene")]
        public event SceneChangedHandler SceneChanged
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SceneChanged");
                _SceneChanged += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SceneChanged");
                _SceneChanged -= value;
            }
        }

        private event SceneChangedHandler _SceneChanged;

        /// <summary>
        /// Called after a new Scene has been loaded
        /// </summary>
        /// <remarks>N/A</remarks>
        internal void OnSceneChanged(string targetScene)
        {
            Logger.LogFine("[API] - OnSceneChanged Invoked");

            if (_SceneChanged == null) return;
            
            SceneChangedHandler[] invocationList = (SceneChangedHandler[]) _SceneChanged.GetInvocationList();
            
            foreach (SceneChangedHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(targetScene);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called right before a scene gets loaded, can change which scene gets loaded
        /// </summary>
        /// <remarks>N/A</remarks>
        [HookInfo("Called right before a scene gets loaded, can change which scene gets loaded", "GameManager.LoadScene")]
        public event BeforeSceneLoadHandler BeforeSceneLoadHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding BeforeSceneLoadHook");
                _BeforeSceneLoadHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing BeforeSceneLoadHook");
                _BeforeSceneLoadHook -= value;
            }
        }

        private event BeforeSceneLoadHandler _BeforeSceneLoadHook;

        /// <summary>
        /// Called right before a scene gets loaded, can change which scene gets loaded
        /// </summary>
        /// <remarks>N/A</remarks>
        internal string BeforeSceneLoad(string sceneName)
        {
            Logger.LogFine("[API] - BeforeSceneLoad Invoked");

            if (_BeforeSceneLoadHook == null) return sceneName;

            BeforeSceneLoadHandler[] invocationList = (BeforeSceneLoadHandler[]) _BeforeSceneLoadHook.GetInvocationList();
            
            foreach (BeforeSceneLoadHandler toInvoke in invocationList)
            {
                try
                {
                    sceneName = toInvoke.Invoke(sceneName);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return sceneName;
        }

        #endregion

        /// <summary>
        /// Called whenever game tries to show cursor
        /// </summary>
        [HookInfo("Called whenever game tries to show cursor", "InputHandler.OnGUI")]
        public event CursorHandler CursorHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding CursorHook");
                _CursorHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing CursorHook");
                _CursorHook -= value;
            }
        }

        private event CursorHandler _CursorHook;

        /// <summary>
        /// Called whenever game tries to show cursor
        /// </summary>
        internal void OnCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            
            if (_CursorHook != null)
            {
                _CursorHook.Invoke();
                return;
            }

            if (GameManager.instance.isPaused)
            {
                Cursor.visible = true;
                return;
            }

            Cursor.visible = false;
        }


        /// <summary>
        /// Called whenever a new gameobject is created with a collider and playmaker2d
        /// </summary>
        /// <remarks>PlayMakerUnity2DProxy.Start</remarks>
        [HookInfo("Called whenever a new gameobject is created with a collider and playmaker2d", "PlayMakerUnity2DProxy.Start")]
        public event ColliderCreateHandler ColliderCreateHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding ColliderCreateHook");
                _ColliderCreateHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing ColliderCreateHook");
                _ColliderCreateHook -= value;
            }
        }

        private event ColliderCreateHandler _ColliderCreateHook;

        /// <summary>
        /// Called whenever a new gameobject is created with a collider and playmaker2d
        /// </summary>
        /// <remarks>PlayMakerUnity2DProxy.Start</remarks>
        internal void OnColliderCreate(GameObject go)
        {
            Logger.LogFine("[API] - OnColliderCreate Invoked");

            if (_ColliderCreateHook == null) return;
            
            ColliderCreateHandler[] invocationList = (ColliderCreateHandler[]) _ColliderCreateHook.GetInvocationList();
            
            foreach (ColliderCreateHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(go);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }


        /// <summary>
        /// Called whenever game tries to create a new gameobject.  This happens often, care should be taken.
        /// </summary>
        [HookInfo("Called whenever game tries to create a new gameobject.  This happens often, care should be taken.", "ObjectPool.Spawn")]
        public event GameObjectHandler ObjectPoolSpawnHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding ObjectPoolSpawnHook");
                _ObjectPoolSpawnHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing ObjectPoolSpawnHook");
                _ObjectPoolSpawnHook -= value;
            }
        }

        private event GameObjectHandler _ObjectPoolSpawnHook;

        /// <summary>
        /// Called whenever game tries to show cursor
        /// </summary>
        internal GameObject OnObjectPoolSpawn(GameObject go)
        {
            // No log because it's too spammy
            
            if (_ObjectPoolSpawnHook == null) return go;

            GameObjectHandler[] invocationList = (GameObjectHandler[]) _ObjectPoolSpawnHook.GetInvocationList();
            
            foreach (GameObjectHandler toInvoke in invocationList)
            {
                try
                {
                    go = toInvoke.Invoke(go);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return go;
        }


        /// <summary>
        /// Called whenever game sends GetEventSender. 
        /// </summary>
        /// <remarks>HutongGames.PlayMaker.Actions.GetEventSender</remarks>
        [HookInfo("Called whenever game sends GetEventSender. ", "HutongGames.PlayMaker.Actions.GetEventSender")]
        public event GameObjectFsmHandler OnGetEventSenderHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding OnGetEventSenderHook");
                _OnGetEventSenderHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing OnGetEventSenderHook");
                _OnGetEventSenderHook -= value;
            }
        }

        private event GameObjectFsmHandler _OnGetEventSenderHook;

        /// <summary>
        /// Called whenever the FSM OnGetEvent is ran (only done during attacks/spells right now).  
        /// </summary>
        internal GameObject OnGetEventSender(GameObject go, HutongGames.PlayMaker.Fsm fsm)
        {
            Logger.LogFine("[API] - OnGetEventSendr Invoked");
            
            if (_OnGetEventSenderHook == null) return go;

            GameObjectFsmHandler[] invocationList = (GameObjectFsmHandler[]) _OnGetEventSenderHook.GetInvocationList();
            
            foreach (GameObjectFsmHandler toInvoke in invocationList)
            {
                try
                {
                    go = toInvoke.Invoke(go, fsm);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return go;
        }

        /// <summary>
        /// Called when the game is fully closed
        /// </summary>
        /// <remarks>GameManager.OnApplicationQuit</remarks>
        [HookInfo("Called when the game is fully closed", "GameManager.OnApplicationQuit")]
        public event ApplicationQuitHandler ApplicationQuitHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding ApplicationQuitHook");
                _ApplicationQuitHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing ApplicationQuitHook");
                _ApplicationQuitHook -= value;
            }
        }

        private event ApplicationQuitHandler _ApplicationQuitHook;

        /// <summary>
        /// Called when the game is fully closed
        /// </summary>
        /// <remarks>GameManager.OnApplicationQuit</remarks>
        internal void OnApplicationQuit()
        {
            Logger.LogFine("[API] - OnApplicationQuit Invoked");

            if (_ApplicationQuitHook == null) return;
            
            ApplicationQuitHandler[] invocationList = (ApplicationQuitHandler[]) _ApplicationQuitHook.GetInvocationList();
            
            foreach (ApplicationQuitHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called when the game changes to a new regional font
        /// </summary>
        /// <remarks>ChangeFontByLanguage.SetFont</remarks>
        [HookInfo("Called when the game changes to a new regional font", "ChangeFontByLanguage.SetFont")]
        public event SetFontHandler SetFontHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding SetFontHook");
                _SetFontHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing SetFontHook");
                _SetFontHook -= value;
            }
        }

        private event SetFontHandler _SetFontHook;

        /// <summary>
        /// Called when the game changes to a new regional font
        /// </summary>
        /// <remarks>ChangeFontByLanguage.SetFont</remarks>
        internal void OnSetFont()
        {
            Logger.LogFine("[API] - OnSetFont Invoked");

            if (_SetFontHook == null) return;
            
            SetFontHandler[] invocationList = (SetFontHandler[]) _SetFontHook.GetInvocationList();
            
            foreach (SetFontHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        /// <summary>
        /// Called when TMP_Text.isRightToLeftText is requested
        /// </summary>
        /// <remarks>TMPro.TMP_Text.isRightToLeftText</remarks>
        [HookInfo("Called when TMP_Text.isRightToLeftText is requested", "TMPro.TMP_Text.isRightToLeftText")]
        public event TextDirectionProxy TextDirectionHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding TextDirectionHook");
                _TextDirectionHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing TextDirectionHook");
                _TextDirectionHook -= value;
            }
        }

        private event TextDirectionProxy _TextDirectionHook;

        /// <summary>
        /// Called when TMP_Text.isRightToLeftText is requested
        /// </summary>
        /// <param name="direction">The currently set text direction</param>
        /// <return>Modified text direction</return>
        internal bool GetTextDirection(bool direction)
        {
            Logger.LogFine("[API] - GetTextDirection Invoked");

            bool result = direction;
            bool changedValue = false;
            if (_TextDirectionHook == null) return result;

            TextDirectionProxy[] invocationList = (TextDirectionProxy[]) _TextDirectionHook.GetInvocationList();
            
            foreach (TextDirectionProxy toInvoke in invocationList)
            {
                try
                {
                    bool invokeValue = toInvoke.Invoke(direction);
                    
                    if (invokeValue == direction || changedValue) continue;

                    result = invokeValue;
                    changedValue = true;
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return result;
        }

        /// <summary>
        /// Save GlobalSettings to disk. (backs up the current global settings if it exists)
        /// </summary>
        internal void SaveGlobalSettings()
        {
            Logger.Log("Saving Global Settings");
            if (File.Exists(SettingsPath + ".bak"))
                File.Delete(SettingsPath + ".bak");

            if (File.Exists(SettingsPath))
                File.Move(SettingsPath, SettingsPath + ".bak");

            using (FileStream fileStream = File.Create(SettingsPath))
            {
                using (StreamWriter writer = new StreamWriter(fileStream))
                {
                    string text4 = JsonUtility.ToJson(GlobalSettings, true);
                    writer.Write(text4);
                }
            }
        }

        /// <summary>
        /// Loads global settings from disk (if they exist)
        /// </summary>
        internal void LoadGlobalSettings()
        {
            Logger.Log("Loading ModdingApi Global Settings.");

            if (!File.Exists(SettingsPath))
            {
                _globalSettings = new ModHooksGlobalSettings {LoggingLevel = LogLevel.Info, ModEnabledSettings = new SerializableBoolDictionary()};
                return;
            }

            try
            {
                //Logger.Log("[API] - Loading Global Settings");
                using (FileStream fileStream = File.OpenRead(SettingsPath))
                {
                    using (StreamReader reader = new StreamReader(fileStream))
                    {
                        string json = reader.ReadToEnd();
                        _globalSettings = JsonUtility.FromJson<ModHooksGlobalSettings>(json);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("[API] - Failed to load global settings, creating new settings file:\n" + e);

                if (File.Exists(SettingsPath))
                {
                    File.Move(SettingsPath, SettingsPath + ".error");
                }

                _globalSettings = new ModHooksGlobalSettings {LoggingLevel = LogLevel.Info, ModEnabledSettings = new SerializableBoolDictionary()};
            }
        }

        [HookInfo("Called whenever a HitInstance is created. Overrides normal functionality", "HutongGames.PlayMaker.Actions.TakeDamage")]
        public event HitInstanceHandler HitInstanceHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding HitInstanceHook");
                _HitInstanceHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing HitInstanceHook");
                _HitInstanceHook -= value;
            }
        }

        private event HitInstanceHandler _HitInstanceHook;

        /// <summary>
        /// Called whenever a HitInstance is created. Overrides normal functionality
        /// </summary>
        /// <remarks>HutongGames.PlayMaker.Actions.TakeDamage</remarks>
        internal HitInstance OnHitInstanceBeforeHit(HutongGames.PlayMaker.Fsm owner, HitInstance hit)
        {
            Logger.LogFine("[API] - OnHitInstance Invoked");

            if (_HitInstanceHook == null) return hit;

            HitInstanceHandler[] invocationList = (HitInstanceHandler[]) _HitInstanceHook.GetInvocationList();
            
            foreach (HitInstanceHandler toInvoke in invocationList)
            {
                try
                {
                    hit = toInvoke.Invoke(owner, hit);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return hit;
        }


        [HookInfo(
            "Called when a SceneManager calls DrawBlackBorders and creates boarders for a scene. " +
            "You may use or modify the bounds of an area of the scene with these.",
            "SceneManager.DrawBlackBorders"
        )]
        public event DrawBlackBordersHandler DrawBlackBordersHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding DrawBlackBordersHook");
                _DrawBlackBordersHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing DrawBlackBordersHook");
                _DrawBlackBordersHook -= value;
            }
        }

        private event DrawBlackBordersHandler _DrawBlackBordersHook;

        /// <summary>
        /// Called when a SceneManager calls DrawBlackBorders and creates boarders for a scene. You may use or modify the bounds of an area of the scene with these.
        /// </summary>
        /// <remarks>SceneManager.DrawBlackBorders</remarks>
        internal void OnDrawBlackBorders(List<GameObject> borders)
        {
            Logger.LogFine("[API] - OnDrawBlackBorders Invoked");

            if (_DrawBlackBordersHook == null) return;

            DrawBlackBordersHandler[] invocationList = (DrawBlackBordersHandler[]) _DrawBlackBordersHook.GetInvocationList();
            
            foreach (DrawBlackBordersHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(borders);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }

        [HookInfo(
            "Called when an enemy is enabled. " +
            "Check this isDead flag to see if they're already dead. " +
            "If you return true, this will mark the enemy as already dead on load. Default behavior is to return the value inside \"isAlreadyDead\".",
            "HealthManager.CheckPersistence"
        )]
        public event OnEnableEnemyHandler OnEnableEnemyHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding OnEnableEnemyHook");
                _OnEnableEnemyHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing OnEnableEnemyHook");
                _OnEnableEnemyHook -= value;
            }
        }

        private event OnEnableEnemyHandler _OnEnableEnemyHook;

        /// <summary>
        /// Called when an enemy is enabled. Check this isDead flag to see if they're already dead. If you return true, this will mark the enemy as already dead on load. Default behavior is to return the value inside "isAlreadyDead".
        /// </summary>
        /// <remarks>HealthManager.CheckPersistence</remarks>
        internal bool OnEnableEnemy(GameObject enemy, bool isAlreadyDead)
        {
            Logger.LogFine("[API] - OnEnableEnemy Invoked");

            if (_OnEnableEnemyHook == null) return isAlreadyDead;

            OnEnableEnemyHandler[] invocationList = (OnEnableEnemyHandler[]) _OnEnableEnemyHook.GetInvocationList();
            
            foreach (OnEnableEnemyHandler toInvoke in invocationList)
            {
                try
                {
                    isAlreadyDead = toInvoke.Invoke(enemy, isAlreadyDead);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }

            return isAlreadyDead;
        }


        [HookInfo(
            "Called when an enemy recieves a death event. " +
            "It looks like this event may be called multiple times on an enemy, " +
            "so check \"eventAlreadyRecieved\" to see if the event has been fired more than once.",
            "EnemyDeathEffects.RecieveDeathEvent"
        )]
        public event OnRecieveDeathEventHandler OnRecieveDeathEventHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding OnRecieveDeathEventHook");
                _OnRecieveDeathEventHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing OnRecieveDeathEventHook");
                _OnRecieveDeathEventHook -= value;
            }
        }

        private event OnRecieveDeathEventHandler _OnRecieveDeathEventHook;

        /// <summary>
        /// Called when an enemy recieves a death event. It looks like this event may be called multiple times on an enemy, so check "eventAlreadyRecieved" to see if the event has been fired more than once.
        /// </summary>
        /// <remarks>EnemyDeathEffects.RecieveDeathEvent</remarks>
        internal void OnRecieveDeathEvent
        (
            EnemyDeathEffects enemyDeathEffects,
            bool eventAlreadyRecieved,
            ref float? attackDirection,
            ref bool resetDeathEvent,
            ref bool spellBurn,
            ref bool isWatery
        )
        {
            Logger.LogFine("[API] - OnRecieveDeathEvent Invoked");

            if (_OnRecieveDeathEventHook == null) return;

            OnRecieveDeathEventHandler[] invocationList = (OnRecieveDeathEventHandler[]) _OnRecieveDeathEventHook.GetInvocationList();
            
            foreach (OnRecieveDeathEventHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke(enemyDeathEffects, eventAlreadyRecieved, ref attackDirection, ref resetDeathEvent, ref spellBurn, ref isWatery);
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }


        [HookInfo(
            "Called when an enemy dies and a journal kill is recorded. " +
            "You may use the \"playerDataName\" string or one of the additional pre-formatted player data strings to look up values in playerData.",
            "EnemyDeathEffects.OnRecordKillForJournal"
        )]
        public event OnRecordKillForJournalHandler OnRecordKillForJournalHook
        {
            add
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Adding OnRecordKillForJournalHook");
                _OnRecordKillForJournalHook += value;
            }
            remove
            {
                Logger.LogDebug($"[{value.Method.DeclaringType?.Name}] - Removing OnRecordKillForJournalHook");
                _OnRecordKillForJournalHook -= value;
            }
        }

        private event OnRecordKillForJournalHandler _OnRecordKillForJournalHook;

        /// <summary>
        /// Called when an enemy dies and a journal kill is recorded. You may use the "playerDataName" string or one of the additional pre-formatted player data strings to look up values in playerData.
        /// </summary>
        /// <remarks>EnemyDeathEffects.OnRecordKillForJournal</remarks>
        internal void OnRecordKillForJournal
        (
            EnemyDeathEffects enemyDeathEffects,
            string playerDataName,
            string killedBoolPlayerDataLookupKey,
            string killCountIntPlayerDataLookupKey,
            string newDataBoolPlayerDataLookupKey
        )
        {
            Logger.LogFine("[API] - RecordKillForJournal Invoked");

            if (_OnRecordKillForJournalHook == null) return;

            OnRecordKillForJournalHandler[] invocationList = (OnRecordKillForJournalHandler[]) _OnRecordKillForJournalHook.GetInvocationList();
            
            foreach (OnRecordKillForJournalHandler toInvoke in invocationList)
            {
                try
                {
                    toInvoke.Invoke
                    (
                        enemyDeathEffects,
                        playerDataName,
                        killedBoolPlayerDataLookupKey,
                        killCountIntPlayerDataLookupKey,
                        newDataBoolPlayerDataLookupKey
                    );
                }
                catch (Exception ex)
                {
                    Logger.LogError("[API] - " + ex);
                }
            }
        }
    }
}