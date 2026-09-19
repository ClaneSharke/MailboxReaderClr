using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EwsToSqlSync
{
    /// <summary>
    /// Standalone alternative to MailboxReaderClr's SQLCLR proc: reads an on-premises
    /// Exchange mailbox via EWS and writes new messages into a SQL Server table, using an
    /// ordinary SQL login -- no SQL Server CLR, no assembly registration, no trust hashes.
    /// Meant to be run on a schedule via Windows Task Scheduler.
    ///
    /// Usage:
    ///   EwsToSqlSync.exe [path-to-config.json]
    /// If no path is given, looks for config.json next to the .exe.
    ///
    /// Exit codes: 0 = success, 1 = failure (so Task Scheduler can flag a failed run).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            string configPath = args.Length > 0
                ? args[0]
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

            string logPath = null;

            try
            {
                if (!File.Exists(configPath))
                {
                    throw new FileNotFoundException("Config file not found: " + configPath);
                }

                SyncConfig config = SyncConfig.Load(configPath);
                logPath = config.LogFile;

                Log(logPath, "=== EwsToSqlSync run starting " + DateTime.Now.ToString("u") + " ===");
                Log(logPath, "Config: " + configPath);
                Log(logPath, "EwsUrl=" + config.EwsUrl + " MailboxUpn=" + config.MailboxUpn + " Folder=" + config.FolderName + " Top=" + config.Top + " UnreadOnly=" + config.UnreadOnly);

                List<MailMessageRow> messages = EwsClient.FetchMessages(
                    config.EwsUrl,
                    config.MailboxUpn,
                    config.Domain,
                    config.Username,
                    config.Password,
                    config.FolderName,
                    config.Top,
                    config.UnreadOnly);

                Log(logPath, "Fetched " + messages.Count + " message(s) from EWS.");

                int inserted = 0, skipped = 0;
                UpsertMessages(config, messages, out inserted, out skipped);

                Log(logPath, "Done. Inserted " + inserted + " new row(s), skipped " + skipped + " already-present row(s).");
                Log(logPath, "=== EwsToSqlSync run finished OK " + DateTime.Now.ToString("u") + " ===");
                return 0;
            }
            catch (Exception ex)
            {
                Log(logPath, "ERROR: " + ex.Message);
                Log(logPath, ex.ToString());
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Inserts any message not already present (keyed on MessageId), so re-running this
        /// on a schedule against an inbox that still has yesterday's messages in it doesn't
        /// create duplicate rows.
        /// </summary>
        private static void UpsertMessages(SyncConfig config, List<MailMessageRow> messages, out int inserted, out int skipped)
        {
            inserted = 0;
            skipped = 0;
            if (messages.Count == 0)
            {
                return;
            }

            string table = ValidateAndQuoteIdentifier(config.TableName);

            using (var conn = new SqlConnection(config.SqlConnectionString))
            {
                conn.Open();

                string sql =
                    "IF NOT EXISTS (SELECT 1 FROM " + table + " WHERE MessageId = @MessageId) " +
                    "INSERT INTO " + table +
                    " (MessageId, Subject, FromAddress, FromName, ReceivedDateTime, IsRead, HasAttachments, BodyPreview) " +
                    "VALUES (@MessageId, @Subject, @FromAddress, @FromName, @ReceivedDateTime, @IsRead, @HasAttachments, @BodyPreview);" +
                    "SELECT @@ROWCOUNT;";

                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@MessageId", SqlDbType.NVarChar, 400);
                    cmd.Parameters.Add("@Subject", SqlDbType.NVarChar, 1000);
                    cmd.Parameters.Add("@FromAddress", SqlDbType.NVarChar, 320);
                    cmd.Parameters.Add("@FromName", SqlDbType.NVarChar, 200);
                    cmd.Parameters.Add("@ReceivedDateTime", SqlDbType.DateTime2);
                    cmd.Parameters.Add("@IsRead", SqlDbType.Bit);
                    cmd.Parameters.Add("@HasAttachments", SqlDbType.Bit);
                    cmd.Parameters.Add("@BodyPreview", SqlDbType.NVarChar, 4000);

                    foreach (MailMessageRow msg in messages)
                    {
                        cmd.Parameters["@MessageId"].Value = (object)msg.MessageId ?? DBNull.Value;
                        cmd.Parameters["@Subject"].Value = (object)msg.Subject ?? DBNull.Value;
                        cmd.Parameters["@FromAddress"].Value = (object)msg.FromAddress ?? DBNull.Value;
                        cmd.Parameters["@FromName"].Value = (object)msg.FromName ?? DBNull.Value;
                        cmd.Parameters["@ReceivedDateTime"].Value = msg.ReceivedDateTime == default(DateTime) ? (object)DBNull.Value : msg.ReceivedDateTime;
                        cmd.Parameters["@IsRead"].Value = msg.IsRead;
                        cmd.Parameters["@HasAttachments"].Value = msg.HasAttachments;
                        cmd.Parameters["@BodyPreview"].Value = (object)msg.BodyPreview ?? DBNull.Value;

                        object result = cmd.ExecuteScalar();
                        int rowsAffected = (result == null || result == DBNull.Value) ? 0 : Convert.ToInt32(result);
                        if (rowsAffected > 0)
                        {
                            inserted++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Table name comes from a local config file, but it still gets interpolated
        /// directly into SQL text (table names can't be parameters), so validate it's a
        /// plain identifier -- optionally schema-qualified -- before use.
        /// </summary>
        private static string ValidateAndQuoteIdentifier(string tableName)
        {
            if (string.IsNullOrEmpty(tableName))
            {
                throw new ArgumentException("TableName is required in config.json.");
            }

            string[] parts = tableName.Split('.');
            if (parts.Length > 2)
            {
                throw new ArgumentException("TableName must be 'Table' or 'Schema.Table': " + tableName);
            }

            var quotedParts = new List<string>();
            foreach (string part in parts)
            {
                if (!Regex.IsMatch(part, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                {
                    throw new ArgumentException("Invalid identifier in TableName: '" + part + "'");
                }
                quotedParts.Add("[" + part + "]");
            }
            return string.Join(".", quotedParts.ToArray());
        }

        private static void Log(string logPath, string message)
        {
            string line = "[" + DateTime.Now.ToString("u") + "] " + message;
            Console.WriteLine(line);

            if (string.IsNullOrEmpty(logPath))
            {
                return;
            }

            try
            {
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging to file is best-effort; never let a log write failure mask the
                // real error or crash a scheduled run.
            }
        }
    }
}
