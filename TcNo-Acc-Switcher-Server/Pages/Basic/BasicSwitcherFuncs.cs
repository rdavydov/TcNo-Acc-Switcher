﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TcNo_Acc_Switcher_Globals;
using TcNo_Acc_Switcher_Server.Data;
using TcNo_Acc_Switcher_Server.Pages.General;

namespace TcNo_Acc_Switcher_Server.Pages.Basic
{
    public class BasicSwitcherFuncs
    {
        private static readonly Lang Lang = Lang.Instance;

        private static readonly Data.Settings.Basic Basic = Data.Settings.Basic.Instance;
        private static readonly CurrentPlatform Platform = CurrentPlatform.Instance;
        /// <summary>
        /// Main function for Basic Account Switcher. Run on load.
        /// Collects accounts from cache folder
        /// Prepares HTML Elements string for insertion into the account switcher GUI.
        /// </summary>
        /// <returns>Whether account loading is successful, or a path reset is needed (invalid dir saved)</returns>
        public static void LoadProfiles()
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.LoadProfiles] Loading Basic profiles for: " + Platform.FullName);
            Data.Settings.Basic.Instance.LoadFromFile();
            _ = GenericFunctions.GenericLoadAccounts(Platform.FullName, true);
        }

        /// <summary>
        /// Used in JS. Gets whether forget account is enabled (Whether to NOT show prompt, or show it).
        /// </summary>
        /// <returns></returns>
        [JSInvokable]
        public static Task<bool> GetBasicForgetAcc() => Task.FromResult(Basic.ForgetAccountEnabled);

        #region Account IDs

        public static Dictionary<string, string> AccountIds;
        public static void LoadAccountIds() => AccountIds = GeneralFuncs.ReadDict(Platform.IdsJsonPath);
        private static void SaveAccountIds() =>
            File.WriteAllText(Platform.IdsJsonPath, JsonConvert.SerializeObject(AccountIds));
        public static string GetNameFromId(string accId) => AccountIds.ContainsKey(accId) ? AccountIds[accId] : accId;
        #endregion

        /// <summary>
        /// Restart Basic with a new account selected. Leave args empty to log into a new account.
        /// </summary>
        /// <param name="accId">(Optional) User's unique account ID</param>
        /// <param name="args">Starting arguments</param>
        [SupportedOSPlatform("windows")]
        public static void SwapBasicAccounts(string accId = "", string args = "")
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.SwapBasicAccounts] Swapping to: hidden.");
            // Handle args:
            if (Platform.ExeExtraArgs != "")
            {
                args = Platform.ExeExtraArgs + (args == "" ? "" : " " + args);
            }

            LoadAccountIds();
            var accName = GetNameFromId(accId);

            // Kill game processes
            _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_ClosingPlatform", new { platform = "Basic" }]);
            if (!GeneralFuncs.CloseProcesses(Platform.ExesToEnd, Data.Settings.Basic.Instance.AltClose))
            {
                _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_ClosingPlatformFailed", new { platform = Platform.FullName }]);
                return;
            };

            // Add currently logged in account if there is a way of checking unique ID.
            // If saved, and has unique key: Update
            if (Platform.UniqueIdFile is not null)
            {
                string uniqueId;
                if (Platform.UniqueIdMethod is "REGKEY" && !string.IsNullOrEmpty(Platform.UniqueIdFile))
                {
                    _ = ReadRegistryKeyWithErrors(Platform.UniqueIdFile, out uniqueId);
                }
                else
                    uniqueId = GetUniqueId();

                // UniqueId Found >> Save!
                if (File.Exists(Platform.IdsJsonPath))
                {
                    if (!string.IsNullOrEmpty(uniqueId) && AccountIds.ContainsKey(uniqueId))
                    {
                        if (accId == uniqueId)
                        {
                            _ = GeneralInvocableFuncs.ShowToast("info", Lang["Toast_AlreadyLoggedIn"], renderTo: "toastarea");
                            GeneralFuncs.StartProgram(Basic.Exe(), Basic.Admin, args);
                            return;
                        }
                        BasicAddCurrent(AccountIds[uniqueId]);
                    }
                }
            }

            // Clear current login
            ClearCurrentLoginBasic();

            // Copy saved files in
            if (accName != "")
            {
                if (!BasicCopyInAccount(accId)) return;
                Globals.AddTrayUser(Platform.SafeName, $"+{Platform.PrimaryId}:" + accId, accName, Basic.TrayAccNumber); // Add to Tray list, using first Identifier
            }

            _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_StartingPlatform", new { platform = Platform.FullName }]);
            GeneralFuncs.StartProgram(Basic.Exe(), Basic.Admin, args);

            Globals.RefreshTrayArea();
            _ = AppData.InvokeVoidAsync("updateStatus", Lang["Done"]);
        }

        [SupportedOSPlatform("windows")]
        private static bool ClearCurrentLoginBasic()
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.ClearCurrentLoginBasic]");

            foreach (var accFile in Platform.PathListToClear)
            {
                // The "file" is a registry key
                if (accFile.StartsWith("REG:"))
                {
                    if (!Globals.SetRegistryKey(accFile[4..])) // Remove "REG:" and read data
                    {
                        _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_RegFailWrite"], Lang["Error"], "toastarea");
                        return false;
                    }
                    continue;
                }


                // Handle wildcards
                if (accFile.Contains("*"))
                {
                    var folder = Environment.ExpandEnvironmentVariables(Path.GetDirectoryName(accFile) ?? "");
                    var file = Path.GetFileName(accFile);

                    // Handle "...\\*" folder.
                    if (file == "*")
                    {
                        if (!Directory.Exists(Path.GetDirectoryName(accFile))) return false;
                        Globals.RecursiveDelete(Path.GetDirectoryName(accFile), false);
                        continue;
                    }

                    // Handle "...\\*.log" or "...\\file_*", etc.
                    // This is NOT recursive - Specify folders manually in JSON
                    foreach (var f in Directory.GetFiles(folder, file))
                        if (File.Exists(f)) File.Delete(f);

                    continue;
                }

                var fullPath = Environment.ExpandEnvironmentVariables(accFile);
                // Is folder? Recursive copy folder
                if (Directory.Exists(accFile))
                {
                    Globals.RecursiveDelete(Path.GetDirectoryName(fullPath), false);
                    continue;
                }

                // Is file? Delete file
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }

            if (Platform.UniqueIdMethod != "CREATE_ID_FILE") return true;

            // Unique ID file --> This needs to be deleted for a new instance
            var uniqueIdFile = Platform.GetUniqueFilePath();
            if (File.Exists(uniqueIdFile)) File.Delete(uniqueIdFile);

            return true;
        }

        [SupportedOSPlatform("windows")]
        private static bool BasicCopyInAccount(string accId)
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.BasicCopyInAccount]");
            LoadAccountIds();
            var accName = GetNameFromId(accId);

            var localCachePath = Platform.AccountLoginCachePath(accName);
            _ = Directory.CreateDirectory(localCachePath);

            if (Platform.LoginFiles == null) throw new Exception("No data in basic platform: " + Platform.FullName);

            // Get unique ID from IDs file if unique ID is a registry key. Set if exists.
            if (Platform.UniqueIdMethod is "REGKEY" && !string.IsNullOrEmpty(Platform.UniqueIdFile))
            {
                var uniqueId = GeneralFuncs.ReadDict(Platform.SafeName).FirstOrDefault(x => x.Value == accName).Key;

                if (!string.IsNullOrEmpty(uniqueId) && !Globals.SetRegistryKey(Platform.UniqueIdFile, uniqueId)) // Remove "REG:" and read data
                {
                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_AlreadyLoggedIn"], Lang["Error"], "toastarea");
                    return false;
                }
            }

            var regJson = Platform.HasRegistryFiles ? Platform.ReadRegJson(accName) : new Dictionary<string, string>();

            foreach (var (accFile, savedFile) in Platform.LoginFiles)
            {
                // The "file" is a registry key
                if (accFile.StartsWith("REG:"))
                {
                    var regValue = regJson[accFile] ?? "";

                    if (!Globals.SetRegistryKey(accFile[4..], regValue)) // Remove "REG:" and read data
                    {
                        _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_RegFailWrite"], Lang["Error"], "toastarea");
                        return false;
                    }
                    continue;
                }

                // FILE OR FOLDER
                HandleFileOrFolder(accFile, savedFile, localCachePath, true);
            }

            return true;
        }

        [SupportedOSPlatform("windows")]
        public static bool BasicAddCurrent(string accName)
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.BasicAddCurrent]");
            if (Platform.ExitBeforeInteract)
            {
                // Kill game processes
                _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_ClosingPlatform", new { platform = "Basic" }]);
                if (!GeneralFuncs.CloseProcesses(Platform.ExesToEnd, Data.Settings.Basic.Instance.AltClose))
                {
                    _ = AppData.InvokeVoidAsync("updateStatus", Lang["Status_ClosingPlatformFailed", new { platform = Platform.FullName }]);
                    return false;
                };
            }

            // Separate special arguments (if any)
            var specialString = "";
            if (Platform.HasExtras && accName.Contains(":{"))
            {
                var index = accName.IndexOf(":{")! + 1;
                specialString = accName[index..];
                accName = accName.Split(":{")[0];
            }

            var localCachePath = Platform.AccountLoginCachePath(accName);
            _ = Directory.CreateDirectory(localCachePath);

            if (Platform.LoginFiles == null) throw new Exception("No data in basic platform: " + Platform.FullName);

            // Handle unique ID
            var uniqueId = "";
            if (Platform.UniqueIdMethod is "REGKEY" && !string.IsNullOrEmpty(Platform.UniqueIdFile))
            {
                if (!ReadRegistryKeyWithErrors(Platform.UniqueIdFile, out uniqueId))
                    return false;
            }
            else
                uniqueId = GetUniqueId();

            if (uniqueId == "" && Platform.UniqueIdMethod == "CREATE_ID_FILE")
            {
                // Unique ID file, and does not already exist: Therefore create!
                var uniqueIdFile = Platform.GetUniqueFilePath();
                uniqueId = Globals.RandomString(16);
                File.WriteAllText(uniqueIdFile, uniqueId);
            }

            // Handle special args in username
            var hadSpecialProperties = ProcessSpecialAccName(specialString, accName, uniqueId);


            var regJson = Platform.HasRegistryFiles ? Platform.ReadRegJson(accName) : new Dictionary<string, string>();

            foreach (var (accFile, savedFile) in Platform.LoginFiles)
            {
                // HANDLE REGISTRY KEY
                if (accFile.StartsWith("REG:"))
                {
                    var trimmedName = accFile[4..];

                    if (ReadRegistryKeyWithErrors(trimmedName, out var response)) // Remove "REG:" and read data
                    {
                        // Write registry value to provided file
                        regJson[accFile] = response;
                    }
                    continue;
                }

                // FILE OR FOLDER
                if (HandleFileOrFolder(accFile, savedFile, localCachePath, false)) continue;

                // Could not find file/folder
                _ = GeneralInvocableFuncs.ShowToast("error", Lang["CouldNotFindX", new { x = accFile }], Lang["DirectoryNotFound"], "toastarea");
                return false;

                // TODO: Run some action that can be specified in the BasicPlatforms.json file
                // Add for the start, and end of this function -- To allow 'plugins'?
                // Use reflection?
            }

            Platform.SaveRegJson(regJson, accName);

            var allIds = GeneralFuncs.ReadDict(Platform.IdsJsonPath);
            allIds[uniqueId] = accName;
            File.WriteAllText(Platform.IdsJsonPath, JsonConvert.SerializeObject(allIds));

            // Copy in profile image from default -- As long as not already handled by special arguments
            if (!hadSpecialProperties.Contains("IMAGE|"))
            {
                _ = Directory.CreateDirectory(Path.Join(GeneralFuncs.WwwRoot(), $"\\img\\profiles\\{Platform.SafeName}"));
                var profileImg = Path.Join(GeneralFuncs.WwwRoot(), $"\\img\\profiles\\{Platform.SafeName}\\{Globals.GetCleanFilePath(uniqueId)}.jpg");
                if (!File.Exists(profileImg))
                {
                    var platformImgPath = "\\img\\platform\\default_images\\" + Platform.SafeName + "Default.png";
                    var currentPlatformImgPath = Path.Join(GeneralFuncs.WwwRoot(), platformImgPath);
                    File.Copy(File.Exists(currentPlatformImgPath)
                        ? Path.Join(currentPlatformImgPath)
                        : Path.Join(GeneralFuncs.WwwRoot(), "\\img\\default_images\\BasicDefault.png"), profileImg, true);
                }
            }

            AppData.ActiveNavMan?.NavigateTo("/Basic/?cacheReload&toast_type=success&toast_title=Success&toast_message=" + Uri.EscapeDataString(Lang["Toast_SavedItem", new { item = accName }]), true);
            return true;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="fromPath"></param>
        /// <param name="toPath"></param>
        /// <param name="localCachePath"></param>
        /// <param name="reverse">FALSE: Platform -> LoginCache. TRUE: LoginCache -> Platform</param>
        private static bool HandleFileOrFolder(string fromPath, string toPath, string localCachePath, bool reverse)
        {
            var toFullPath = Path.Join(localCachePath, toPath);

            // Reverse if necessary. Explained in summary above.
            if (reverse && fromPath.Contains("*"))
            {
                (toPath, fromPath) = (fromPath, toPath); // Reverse
                var wildcard = Path.GetFileName(toPath);
                fromPath = Path.Join(localCachePath, fromPath, wildcard);
                toPath = toPath.Replace(wildcard, "");
                toFullPath = toPath;
            }

            // Handle wildcards
            if (fromPath.Contains("*"))
            {
                var folder = Environment.ExpandEnvironmentVariables(Path.GetDirectoryName(fromPath) ?? "");
                var file = Path.GetFileName(fromPath);

                // Handle "...\\*" folder.
                if (file == "*")
                {
                    if (!Directory.Exists(Path.GetDirectoryName(fromPath))) return false;
                    Globals.CopyFilesRecursive(Path.GetDirectoryName(fromPath), toFullPath, true);
                    return true;
                }

                // Handle "...\\*.log" or "...\\file_*", etc.
                // This is NOT recursive - Specify folders manually in JSON
                _ = Directory.CreateDirectory(toFullPath);
                foreach (var f in Directory.GetFiles(folder, file))
                {
                    var fullOutputPath = Path.Join(toFullPath, Path.GetFileName(f));
                    if (File.Exists(f)) File.Copy(f, fullOutputPath, true);
                }

                return true;
            }

            if (reverse)
                (fromPath, toFullPath) = (toFullPath, fromPath);

            var fullPath = Environment.ExpandEnvironmentVariables(fromPath);
            // Is folder? Recursive copy folder
            if (Directory.Exists(fromPath))
            {
                _ = Directory.CreateDirectory(toFullPath);
                Globals.CopyFilesRecursive(Path.GetDirectoryName(fullPath), toFullPath, true);
                return true;
            }

            // Is file? Copy file
            if (File.Exists(fullPath))
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(toFullPath));
                File.Copy(fullPath, toFullPath, true);
                return true;
            }

            return true;
        }

        /// <summary>
        /// Do special actions with AccName, and return cleaned AccName when done.
        /// </summary>
        /// <param name="accName">Account Name:{JSON OBJECT}</param>
        /// <param name="uniqueId">Unique ID of account</param>
        /// <param name="jsonString">JSON string of actions to perform on account</param>
        private static string ProcessSpecialAccName(string jsonString, string accName, string uniqueId)
        {
            // Verify existence of possible extra properties
            var hadSpecialProperties = "";
            if (!Platform.HasExtras) return hadSpecialProperties;
            var specialProperties = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonString);
            if (specialProperties == null) return hadSpecialProperties;

            // HANDLE SPECIAL IMAGE
            var profileImg = Path.Join(GeneralFuncs.WwwRoot(), $"\\img\\profiles\\{Platform.SafeName}\\{Globals.GetCleanFilePath(uniqueId)}.jpg");
            if (specialProperties.ContainsKey("image"))
            {
                var imageIsUrl = Uri.TryCreate(specialProperties["image"], UriKind.Absolute, out var uriResult)
                                 && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
                if (imageIsUrl)
                {
                    // Is url -> Download
                    if (Globals.DownloadFile(specialProperties["image"], profileImg))
                        hadSpecialProperties = "IMAGE|";
                }
                else
                {
                    // Is not url -> Copy file
                    try
                    {
                        if (File.Exists(specialProperties["image"]))
                            File.Copy(specialProperties["image"], profileImg, true);
                        hadSpecialProperties = "IMAGE|";
                    }
                    catch (Exception)
                    {
                        //
                    }
                }
            }

            return hadSpecialProperties;
        }

        public static string GetUniqueId()
        {
            var fileToRead = Platform.GetUniqueFilePath();
            var uniqueId = "";

            if (Platform.UniqueIdMethod is "REGKEY")
            {
                _ = ReadRegistryKeyWithErrors(Platform.UniqueIdFile, out uniqueId);
                return uniqueId;
            }

            if (Platform.UniqueIdMethod is "CREATE_ID_FILE")
            {
                return File.Exists(fileToRead) ? File.ReadAllText(fileToRead) : uniqueId;
            }

            if (Platform.UniqueIdFile is not "" && File.Exists(fileToRead))
            {
                if (Platform.UniqueIdRegex != null)
                {
                    var m = Regex.Match(
                        File.ReadAllText(fileToRead),
                        Platform.UniqueIdRegex, RegexOptions.IgnoreCase);
                    if (m.Success)
                        uniqueId = m.Value;
                }
                else if (Platform.UniqueIdMethod is "FILE_MD5") // TODO: TEST THIS! -- This is used for static files that do not change throughout the lifetime of an account login.
                {
                    if (!Platform.UniqueIdFile.Contains('*')) uniqueId = GeneralFuncs.GetFileMd5(fileToRead);
                    else
                        uniqueId = string.Join('|', (from f in new DirectoryInfo(fileToRead).GetFiles()
                            where f.Name.EndsWith(Platform.UniqueIdFile.Split('*')[1])
                            select GeneralFuncs.GetFileMd5(f.FullName)).ToList());
                }
            }
            else if (uniqueId != "")
                uniqueId = Globals.GetSha256HashString(uniqueId);

            return uniqueId;
        }

        private static bool ReadRegistryKeyWithErrors(string key, out string value)
        {
            value = Globals.ReadRegistryKey(key);
            switch (value)
            {
                case "ERROR-NULL":
                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_AccountIdReg"], Lang["Error"], "toastarea");
                    return false;
                case "ERROR-READ":
                    _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_RegFailRead"], Lang["Error"], "toastarea");
                    return false;
            }

            return true;
        }
        public static void ChangeUsername(string accId, string newName, bool reload = true)
        {
            LoadAccountIds();
            var oldName = GetNameFromId(accId);

            try
            {
                AccountIds[accId] = newName;
                SaveAccountIds();
            }
            catch (Exception)
            {
                _ = GeneralInvocableFuncs.ShowToast("error", Lang["Toast_CantChangeUsername"], Lang["Error"], "toastarea");
                return;
            }

            // No need to rename image as accId. That step is skipped here.
            Directory.Move($"LoginCache\\{Platform.SafeName}\\{oldName}\\", $"LoginCache\\{Platform.SafeName}\\{newName}\\"); // Rename login cache folder

            if (reload) AppData.ActiveNavMan?.NavigateTo("/Basic/?cacheReload&toast_type=success&toast_title=Success&toast_message=" + Uri.EscapeDataString(Lang["Toast_ChangedUsername"]), true);
        }

        public static Dictionary<string, string> ReadAllIds(string path = null)
        {
            Globals.DebugWriteLine(@"[Func:Basic\BasicSwitcherFuncs.ReadAllIds]");
            var s = JsonConvert.SerializeObject(new Dictionary<string, string>());
            path ??= Platform.IdsJsonPath;
            if (!File.Exists(path)) return JsonConvert.DeserializeObject<Dictionary<string, string>>(s);
            try
            {
                s = Globals.ReadAllText(path);
            }
            catch (Exception)
            {
                //
            }

            return JsonConvert.DeserializeObject<Dictionary<string, string>>(s);
        }
    }
}
