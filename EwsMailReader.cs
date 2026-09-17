using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;
using Microsoft.SqlServer.Server;

namespace MailboxReaderClr
{
    /// <summary>
    /// SQLCLR stored procedure that reads messages from an ON-PREMISES Exchange Server
    /// mailbox via EWS (Exchange Web Services) -- the on-prem counterpart to MailReader.cs,
    /// which targets Exchange Online / Microsoft 365 via Microsoft Graph. These are separate
    /// procs because the protocol, auth model, and parameters are genuinely different:
    /// EWS/SOAP over Windows auth here vs. Graph/REST over OAuth2 there.
    ///
    /// Auth: either the SQL Server service account's own Windows identity
    /// (CredentialCache.DefaultNetworkCredentials, when @Username is not supplied), or an
    /// explicit @Domain/@Username/@Password via NTLM/Negotiate. Whichever account is used
    /// must hold the ApplicationImpersonation management role in Exchange, scoped to just
    /// the target mailbox -- see README.md for the Exchange Management Shell commands.
    ///
    /// NOTE: built from EWS's documented FindItem operation, but not yet verified against a
    /// live Exchange server in this environment. Treat the exact SOAP request/response
    /// shape as a strong first draft to validate against your own server, not a guarantee --
    /// see the Troubleshooting section in README.md.
    /// </summary>
    public class EwsMailReader
    {
        private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
        private const string TypesNs = "http://schemas.microsoft.com/exchange/services/2006/types";
        private const string MessagesNs = "http://schemas.microsoft.com/exchange/services/2006/messages";

        [SqlProcedure]
        public static void ReadMailboxEws(
            SqlString ewsUrl,
            SqlString mailboxUpn,
            SqlString domain,
            SqlString username,
            SqlString password,
            SqlString folderName,
            SqlInt32 top,
            SqlBoolean unreadOnly)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            if (ewsUrl.IsNull || ewsUrl.Value.Length == 0 || mailboxUpn.IsNull || mailboxUpn.Value.Length == 0)
            {
                throw new ArgumentException("EwsUrl and MailboxUpn are required.");
            }

            string folder = (folderName.IsNull || folderName.Value.Length == 0) ? "inbox" : folderName.Value;
            int topCount = top.IsNull ? 25 : top.Value;
            if (topCount <= 0 || topCount > 999)
            {
                topCount = 25;
            }
            bool onlyUnread = !unreadOnly.IsNull && unreadOnly.Value;

            string soapRequest = BuildFindItemRequest(mailboxUpn.Value, folder, topCount, onlyUnread);
            string soapResponse = PostSoap(ewsUrl.Value, soapRequest, domain, username, password);
            List<Dictionary<string, string>> messages = ParseFindItemResponse(soapResponse);

            SqlDataRecord record = new SqlDataRecord(
                new SqlMetaData("MessageId", SqlDbType.NVarChar, 400),
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
                foreach (Dictionary<string, string> msg in messages)
                {
                    record.SetString(0, Truncate(GetVal(msg, "Id"), 400));
                    record.SetString(1, Truncate(GetVal(msg, "Subject"), 1000));
                    record.SetString(2, Truncate(GetVal(msg, "FromAddress"), 320));
                    record.SetString(3, Truncate(GetVal(msg, "FromName"), 200));

                    DateTime received;
                    DateTime.TryParse(GetVal(msg, "DateTimeReceived"), CultureInfo.InvariantCulture, DateTimeStyles.None, out received);
                    record.SetDateTime(4, received);

                    record.SetBoolean(5, string.Equals(GetVal(msg, "IsRead"), "true", StringComparison.OrdinalIgnoreCase));
                    record.SetBoolean(6, string.Equals(GetVal(msg, "HasAttachments"), "true", StringComparison.OrdinalIgnoreCase));
                    record.SetString(7, Truncate(GetVal(msg, "Preview"), 4000));

                    SqlContext.Pipe.SendResultsRow(record);
                }
            }
            finally
            {
                SqlContext.Pipe.SendResultsEnd();
            }
        }

        private static string BuildFindItemRequest(string mailboxUpn, string folder, int top, bool unreadOnly)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<soap:Envelope xmlns:soap=\"").Append(SoapNs).Append("\" xmlns:t=\"").Append(TypesNs).Append("\" xmlns:m=\"").Append(MessagesNs).Append("\">");
            sb.Append("<soap:Header>");
            // Exchange2013_SP1 is a broadly-supported baseline; raise it (e.g. Exchange2016)
            // if your server is newer and you want newer-only fields/behavior.
            sb.Append("<t:RequestServerVersion Version=\"Exchange2013_SP1\"/>");
            // Impersonation lets the connecting account (its own Windows identity, or the
            // explicit credentials passed in) act as MailboxUpn -- requires that account to
            // hold ApplicationImpersonation scoped to this mailbox. See README.md.
            sb.Append("<t:ExchangeImpersonation><t:ConnectingSID><t:PrimarySmtpAddress>")
              .Append(XmlEscape(mailboxUpn))
              .Append("</t:PrimarySmtpAddress></t:ConnectingSID></t:ExchangeImpersonation>");
            sb.Append("</soap:Header>");
            sb.Append("<soap:Body>");
            sb.Append("<m:FindItem Traversal=\"Shallow\">");
            sb.Append("<m:ItemShape>");
            sb.Append("<t:BaseShape>IdOnly</t:BaseShape>");
            sb.Append("<t:AdditionalProperties>");
            sb.Append("<t:FieldURI FieldURI=\"item:Subject\"/>");
            sb.Append("<t:FieldURI FieldURI=\"item:DateTimeReceived\"/>");
            sb.Append("<t:FieldURI FieldURI=\"message:IsRead\"/>");
            sb.Append("<t:FieldURI FieldURI=\"item:HasAttachments\"/>");
            sb.Append("<t:FieldURI FieldURI=\"message:Sender\"/>");
            sb.Append("<t:FieldURI FieldURI=\"item:Preview\"/>");
            sb.Append("</t:AdditionalProperties>");
            sb.Append("</m:ItemShape>");
            sb.Append("<m:IndexedPageItemView MaxEntriesReturned=\"").Append(top).Append("\" Offset=\"0\" BasePoint=\"Beginning\"/>");
            if (unreadOnly)
            {
                sb.Append("<m:Restriction><t:IsEqualTo><t:FieldURI FieldURI=\"message:IsRead\"/>")
                  .Append("<t:FieldURIOrConstant><t:Constant Value=\"false\"/></t:FieldURIOrConstant></t:IsEqualTo></m:Restriction>");
            }
            sb.Append("<m:ParentFolderIds><t:DistinguishedFolderId Id=\"").Append(XmlEscape(folder)).Append("\"/></m:ParentFolderIds>");
            sb.Append("</m:FindItem>");
            sb.Append("</soap:Body>");
            sb.Append("</soap:Envelope>");
            return sb.ToString();
        }

        private static string PostSoap(string url, string soapBody, SqlString domain, SqlString username, SqlString password)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(soapBody);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "text/xml; charset=utf-8";
            request.Headers.Add("SOAPAction", "\"http://schemas.microsoft.com/exchange/services/2006/messages/FindItem\"");
            request.ContentLength = bodyBytes.Length;

            bool hasExplicitCreds = !username.IsNull && username.Value.Length > 0 && !password.IsNull;
            if (hasExplicitCreds)
            {
                string domainValue = domain.IsNull ? string.Empty : domain.Value;
                var creds = new CredentialCache();
                var netCred = new NetworkCredential(username.Value, password.Value, domainValue);
                var target = new Uri(url);
                creds.Add(target, "NTLM", netCred);
                creds.Add(target, "Negotiate", netCred);
                request.Credentials = creds;
            }
            else
            {
                // Uses the SQL Server service account's own Windows identity.
                request.UseDefaultCredentials = true;
                request.Credentials = CredentialCache.DefaultNetworkCredentials;
            }

            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(bodyBytes, 0, bodyBytes.Length);
            }

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
                throw new Exception("EWS FindItem request failed: " + details, ex);
            }
        }

        private static List<Dictionary<string, string>> ParseFindItemResponse(string xml)
        {
            var results = new List<Dictionary<string, string>>();

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("soap", SoapNs);
            nsmgr.AddNamespace("t", TypesNs);
            nsmgr.AddNamespace("m", MessagesNs);

            // A malformed/rejected request comes back as a SOAP fault rather than a
            // FindItemResponseMessage -- surface its text instead of silently returning nothing.
            XmlNode faultNode = doc.SelectSingleNode("//soap:Fault", nsmgr);
            if (faultNode != null)
            {
                XmlNode faultString = faultNode.SelectSingleNode("faultstring");
                string faultMsg = faultString != null ? faultString.InnerText : faultNode.OuterXml;
                throw new Exception("EWS SOAP fault: " + faultMsg);
            }

            // A well-formed but unsuccessful FindItem also needs surfacing explicitly.
            XmlNode errorText = doc.SelectSingleNode("//m:FindItemResponseMessage[@ResponseClass='Error']/m:MessageText", nsmgr);
            if (errorText != null)
            {
                throw new Exception("EWS returned an error: " + errorText.InnerText);
            }

            XmlNodeList itemNodes = doc.SelectNodes("//t:Items/*", nsmgr);
            if (itemNodes == null)
            {
                return results;
            }

            foreach (XmlNode item in itemNodes)
            {
                var row = new Dictionary<string, string>();

                XmlNode idAttr = item.SelectSingleNode("t:ItemId/@Id", nsmgr);
                row["Id"] = idAttr != null ? idAttr.Value : string.Empty;

                row["Subject"] = GetChildText(item, "t:Subject", nsmgr);
                row["DateTimeReceived"] = GetChildText(item, "t:DateTimeReceived", nsmgr);
                row["IsRead"] = GetChildText(item, "t:IsRead", nsmgr);
                row["HasAttachments"] = GetChildText(item, "t:HasAttachments", nsmgr);
                row["Preview"] = GetChildText(item, "t:Preview", nsmgr);

                string fromAddress = string.Empty;
                string fromName = string.Empty;
                XmlNode senderMailbox = item.SelectSingleNode("t:Sender/t:Mailbox", nsmgr);
                if (senderMailbox != null)
                {
                    fromAddress = GetChildText(senderMailbox, "t:EmailAddress", nsmgr);
                    fromName = GetChildText(senderMailbox, "t:Name", nsmgr);
                }
                row["FromAddress"] = fromAddress;
                row["FromName"] = fromName;

                results.Add(row);
            }

            return results;
        }

        private static string GetChildText(XmlNode parent, string xpath, XmlNamespaceManager nsmgr)
        {
            XmlNode node = parent.SelectSingleNode(xpath, nsmgr);
            return node != null ? node.InnerText : string.Empty;
        }

        private static string GetVal(Dictionary<string, string> dict, string key)
        {
            string value;
            return dict.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string XmlEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
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
