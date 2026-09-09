using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlTypes;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.SqlServer.Server;

namespace MailboxReaderClr
{
    /// <summary>
    /// SQLCLR stored procedure that reads messages from an Exchange Online / Office 365
    /// mailbox via the Microsoft Graph API.
    ///
    /// Two auth modes, selected by whether @Password is supplied:
    ///   - App-only (default, @Password NULL): client_credentials grant using
    ///     TenantId/ClientId/ClientSecret. Requires the app registration to have the
    ///     Mail.Read (or Mail.ReadBasic.All) Application permission, ideally restricted
    ///     to the target mailbox via an Exchange Online Application Access Policy.
    ///   - Delegated password (ROPC, @Password supplied): resource-owner password
    ///     credentials grant using the mailbox's own username/password. Requires the
    ///     app registration to have the Mail.Read Delegated permission (admin consent),
    ///     "Allow public client flows" enabled (unless also passing ClientSecret for a
    ///     confidential client), and the mailbox account to be excluded from MFA/
    ///     Conditional Access -- ROPC cannot satisfy an MFA challenge. Microsoft
    ///     discourages ROPC for new integrations; prefer app-only where possible.
    ///     See README.md for setup steps and caveats for both modes.
    /// </summary>
    public class MailReader
    {
        [SqlProcedure]
        public static void ReadMailbox(
            SqlString tenantId,
            SqlString clientId,
            SqlString clientSecret,
            SqlString mailboxUpn,
            SqlString password,
            SqlString folderName,
            SqlInt32 top,
            SqlBoolean unreadOnly)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            if (tenantId.IsNull || clientId.IsNull || mailboxUpn.IsNull)
            {
                throw new ArgumentException("TenantId, ClientId and MailboxUpn are all required.");
            }

            bool useDelegatedPassword = !password.IsNull && password.Value.Length > 0;

            if (!useDelegatedPassword && clientSecret.IsNull)
            {
                throw new ArgumentException("ClientSecret is required when Password is not supplied (app-only mode). Pass Password to use delegated username/password auth instead.");
            }

            string folder = (folderName.IsNull || folderName.Value.Length == 0) ? "inbox" : folderName.Value;
            int topCount = top.IsNull ? 25 : top.Value;
            if (topCount <= 0 || topCount > 999)
            {
                topCount = 25;
            }
            bool onlyUnread = !unreadOnly.IsNull && unreadOnly.Value;

            string accessToken;
            string mailboxSegment;

            if (useDelegatedPassword)
            {
                string clientSecretOrNull = clientSecret.IsNull ? null : clientSecret.Value;
                accessToken = GetDelegatedTokenViaPassword(tenantId.Value, clientId.Value, clientSecretOrNull, mailboxUpn.Value, password.Value);
                // A delegated (ROPC) token is bound to the signed-in mailbox itself, so use /me instead of /users/{upn}.
                mailboxSegment = "me";
            }
            else
            {
                accessToken = GetAppOnlyToken(tenantId.Value, clientId.Value, clientSecret.Value);
                mailboxSegment = "users/" + Uri.EscapeDataString(mailboxUpn.Value);
            }

            string filterClause = onlyUnread ? "&$filter=isRead eq false" : string.Empty;

            string url = string.Format(
                "https://graph.microsoft.com/v1.0/{0}/mailFolders/{1}/messages?$top={2}&$select=id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview&$orderby=receivedDateTime desc{3}",
                mailboxSegment,
                Uri.EscapeDataString(folder),
                topCount,
                filterClause);

            string json = GraphGet(url, accessToken);

            var root = (Dictionary<string, object>)JsonMini.Parse(json);

            List<object> items = (root.ContainsKey("value") && root["value"] != null)
                ? (List<object>)root["value"]
                : new List<object>();

            SqlDataRecord record = new SqlDataRecord(
                new SqlMetaData("MessageId", SqlDbType.NVarChar, 200),
                new SqlMetaData("Subject", SqlDbType.NVarChar, 1000),
                new SqlMetaData("FromAddress", SqlDbType.NVarChar, 320),
                new SqlMetaData("FromName", SqlDbType.NVarChar, 200),
                new SqlMetaData("ReceivedDateTime", SqlDbType.DateTime2),
                new SqlMetaData("IsRead", SqlDbType.Bit),
                new SqlMetaData("HasAttachments", SqlDbType.Bit),
                new SqlMetaData("BodyPreview", SqlDbType.NVarChar, 4000)
            );

            SqlContext.Pipe.SendResultsStart(record);

            try
            {
                foreach (object itemObj in items)
                {
                    var item = (Dictionary<string, object>)itemObj;

                    string fromAddress = string.Empty;
                    string fromName = string.Empty;
                    if (item.ContainsKey("from") && item["from"] != null)
                    {
                        var fromObj = (Dictionary<string, object>)item["from"];
                        if (fromObj.ContainsKey("emailAddress") && fromObj["emailAddress"] != null)
                        {
                            var emailAddr = (Dictionary<string, object>)fromObj["emailAddress"];
                            fromAddress = GetString(emailAddr, "address");
                            fromName = GetString(emailAddr, "name");
                        }
                    }

                    DateTime received;
                    DateTime.TryParse(GetString(item, "receivedDateTime"), out received);

                    record.SetString(0, Truncate(GetString(item, "id"), 200));
                    record.SetString(1, Truncate(GetString(item, "subject"), 1000));
                    record.SetString(2, Truncate(fromAddress, 320));
                    record.SetString(3, Truncate(fromName, 200));
                    record.SetDateTime(4, received);
                    record.SetBoolean(5, GetBool(item, "isRead"));
                    record.SetBoolean(6, GetBool(item, "hasAttachments"));
                    record.SetString(7, Truncate(GetString(item, "bodyPreview"), 4000));

                    SqlContext.Pipe.SendResultsRow(record);
                }
            }
            finally
            {
                SqlContext.Pipe.SendResultsEnd();
            }
        }

        private static string GetAppOnlyToken(string tenantId, string clientId, string clientSecret)
        {
            string tokenUrl = string.Format("https://login.microsoftonline.com/{0}/oauth2/v2.0/token", tenantId);

            string body = string.Format(
                "client_id={0}&scope={1}&client_secret={2}&grant_type=client_credentials",
                Uri.EscapeDataString(clientId),
                Uri.EscapeDataString("https://graph.microsoft.com/.default"),
                Uri.EscapeDataString(clientSecret));

            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(tokenUrl);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = bodyBytes.Length;

            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            string responseJson = ReadResponseOrThrow(request, "Token request");

            var tokenResponse = (Dictionary<string, object>)JsonMini.Parse(responseJson);

            if (!tokenResponse.ContainsKey("access_token"))
            {
                throw new Exception("Token response did not contain an access_token: " + responseJson);
            }

            return (string)tokenResponse["access_token"];
        }

        private static string GetDelegatedTokenViaPassword(string tenantId, string clientId, string clientSecretOrNull, string username, string password)
        {
            string tokenUrl = string.Format("https://login.microsoftonline.com/{0}/oauth2/v2.0/token", tenantId);

            var bodyBuilder = new StringBuilder();
            bodyBuilder.Append("client_id=").Append(Uri.EscapeDataString(clientId));
            bodyBuilder.Append("&scope=").Append(Uri.EscapeDataString("https://graph.microsoft.com/Mail.Read offline_access"));
            bodyBuilder.Append("&username=").Append(Uri.EscapeDataString(username));
            bodyBuilder.Append("&password=").Append(Uri.EscapeDataString(password));
            bodyBuilder.Append("&grant_type=password");
            if (!string.IsNullOrEmpty(clientSecretOrNull))
            {
                bodyBuilder.Append("&client_secret=").Append(Uri.EscapeDataString(clientSecretOrNull));
            }

            byte[] bodyBytes = Encoding.UTF8.GetBytes(bodyBuilder.ToString());

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(tokenUrl);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = bodyBytes.Length;

            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(bodyBytes, 0, bodyBytes.Length);
            }

            string responseJson = ReadResponseOrThrow(request, "Delegated (ROPC) token request");

            var tokenResponse = (Dictionary<string, object>)JsonMini.Parse(responseJson);

            if (!tokenResponse.ContainsKey("access_token"))
            {
                throw new Exception("Token response did not contain an access_token: " + responseJson);
            }

            return (string)tokenResponse["access_token"];
        }

        private static string GraphGet(string url, string accessToken)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json";
            request.Headers.Add("Authorization", "Bearer " + accessToken);

            return ReadResponseOrThrow(request, "Graph API request");
        }

        private static string ReadResponseOrThrow(HttpWebRequest request, string context)
        {
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                string details = ex.Message;
                if (ex.Response != null)
                {
                    using (StreamReader reader = new StreamReader(ex.Response.GetResponseStream()))
                    {
                        details = reader.ReadToEnd();
                    }
                }
                throw new Exception(context + " failed: " + details, ex);
            }
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict.ContainsKey(key) && dict[key] != null)
            {
                return dict[key].ToString();
            }
            return string.Empty;
        }

        private static bool GetBool(Dictionary<string, object> dict, string key)
        {
            if (dict.ContainsKey(key) && dict[key] != null)
            {
                return Convert.ToBoolean(dict[key]);
            }
            return false;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }
            return value.Substring(0, maxLength);
        }
    }
}
