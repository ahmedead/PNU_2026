using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPAdminStudio.Models;

namespace SPAdminStudio.Services
{
    public class ProfileStorageService
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SPAdminStudio_Entropy_2026");
        private readonly string _profilesFilePath;

        public ProfileStorageService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = Path.Combine(appData, "SPAdminStudio");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _profilesFilePath = Path.Combine(folder, "profiles.json");
        }

        public List<ConnectionProfile> LoadProfiles()
        {
            try
            {
                if (!File.Exists(_profilesFilePath))
                {
                    return new List<ConnectionProfile>
                    {
                        new ConnectionProfile
                        {
                            ProfileName = "Default Portal",
                            SiteUrl = "http://sharepoint/sites/portal",
                            AuthMode = AuthenticationMode.CurrentWindowsUser
                        }
                    };
                }

                var json = File.ReadAllText(_profilesFilePath);
                var profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(json);
                return profiles ?? new List<ConnectionProfile>();
            }
            catch (Exception ex)
            {
                // Keep the unreadable file so the next save does not silently destroy the user's profiles
                System.Diagnostics.Debug.WriteLine($"Failed to load profiles: {ex.Message}");
                try
                {
                    File.Copy(_profilesFilePath, _profilesFilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: false);
                }
                catch { }
                return new List<ConnectionProfile>();
            }
        }

        public void SaveProfiles(List<ConnectionProfile> profiles)
        {
            try
            {
                var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });

                // Write to a temp file and swap it in, so a crash mid-write cannot truncate the profiles
                var tempPath = _profilesFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _profilesFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save profiles: {ex.Message}");
            }
        }

        public static string EncryptPassword(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword)) return string.Empty;
            try
            {
                var data = Encoding.UTF8.GetBytes(plainPassword);
                var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string DecryptPassword(string encryptedPassword)
        {
            if (string.IsNullOrEmpty(encryptedPassword)) return string.Empty;
            try
            {
                var data = Convert.FromBase64String(encryptedPassword);
                var decrypted = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
