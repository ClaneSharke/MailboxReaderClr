using System;
using System.Collections.Generic;
using System.IO;

namespace EwsToSqlSync
{
    /// <summary>
    /// Settings loaded from config.json. See config.example.json for the shape and comments.
    /// </summary>
    internal class SyncConfig
    {
        public string EwsUrl;
        public string MailboxUpn;
        public string Domain;
        public string Username;
        public string Password;
        public string FolderName = "inbox";
        public int Top = 25;
        public bool UnreadOnly = false;

        public string SqlConnectionString;
        public string TableName = "dbo.MailboxMessages";

        /// <summary>Optional. If set, run output is appended here as well as written to stdout.</summary>
        public string LogFile;

        public static SyncConfig Load(string path)
        {
            string json = File.ReadAllText(path);
            object parsed = JsonMini.Parse(json);
            var dict = parsed as Dictionary<string, object>;
            if (dict == null)
            {
                throw new FormatException("config.json must contain a single JSON object.");
            }

            var config = new SyncConfig
            {
                EwsUrl = GetString(dict, "EwsUrl", null),
                MailboxUpn = GetString(dict, "MailboxUpn", null),
                Domain = GetString(dict, "Domain", null),
                Username = GetString(dict, "Username", null),
                Password = GetString(dict, "Password", null),
                FolderName = GetString(dict, "FolderName", "inbox"),
                Top = GetInt(dict, "Top", 25),
                UnreadOnly = GetBool(dict, "UnreadOnly", false),
                SqlConnectionString = GetString(dict, "SqlConnectionString", null),
                TableName = GetString(dict, "TableName", "dbo.MailboxMessages"),
                LogFile = GetString(dict, "LogFile", null)
            };

            var missing = new List<string>();
            if (string.IsNullOrEmpty(config.EwsUrl)) missing.Add("EwsUrl");
            if (string.IsNullOrEmpty(config.MailboxUpn)) missing.Add("MailboxUpn");
            if (string.IsNullOrEmpty(config.SqlConnectionString)) missing.Add("SqlConnectionString");
            if (missing.Count > 0)
            {
                throw new ArgumentException("config.json is missing required field(s): " + string.Join(", ", missing.ToArray()));
            }

            return config;
        }

        private static string GetString(Dictionary<string, object> dict, string key, string defaultValue)
        {
            object value;
            if (dict.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToString(value);
            }
            return defaultValue;
        }

        private static int GetInt(Dictionary<string, object> dict, string key, int defaultValue)
        {
            object value;
            if (dict.TryGetValue(key, out value) && value != null)
            {
                // JsonMini parses all numbers as double
                return Convert.ToInt32(Convert.ToDouble(value));
            }
            return defaultValue;
        }

        private static bool GetBool(Dictionary<string, object> dict, string key, bool defaultValue)
        {
            object value;
            if (dict.TryGetValue(key, out value) && value != null)
            {
                return Convert.ToBoolean(value);
            }
            return defaultValue;
        }
    }
}
