using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;

namespace EwsToSqlSync
{
    /// <summary>
    /// One row read from the mailbox via EWS FindItem.
    /// </summary>
    public class MailMessageRow
    {
        public string MessageId;
        public string Subject;
        public string FromAddress;
        public string FromName;
        public DateTime ReceivedDateTime;
        public bool IsRead;
        public bool HasAttachments;
        public string BodyPreview;
    }

    /// <summary>
    /// Reads messages from an on-premises Exchange Server mailbox via EWS (Exchange Web
    /// Services). This is the same FindItem logic as MailboxReaderClr's EwsMailReader.cs
    /// (the SQLCLR proc), adapted to run as a plain standalone process instead of inside
    /// SQL Server -- returns rows as objects rather than streaming SqlDataRecords, and takes
    /// plain strings for credentials instead of SqlString.
    ///
    /// Auth: either the account this process runs as (CredentialCache.DefaultNetworkCredentials,
    /// when username is not supplied -- e.g. running under a scheduled task's "Run as" account),
    /// or explicit domain/username/password via NTLM/Negotiate. Whichever account is used must
    /// hold the ApplicationImpersonation management role in Exchange, scoped to the target
    /// mailbox -- see README.md.
    ///
    /// NOTE: built from EWS's documented FindItem operation; validate the exact SOAP shape
    /// against your own Exchange version/CU (see README.md troubleshooting).
    /// </summary>
    public static class EwsClient
    {
        private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
        private const string TypesNs = "http://schemas.microsoft.com/exchange/services/2006/types";
        private const string MessagesNs = "http://schemas.microsoft.com/exchange/services/2006/messages";

        public static List<MailMessageRow> FetchMessages(
            string ewsUrl,
            string mailboxUpn,
            string domain,
            string username,
            string password,
            string folderName,
            int top,
            bool unreadOnly)
        {
            if (string.IsNullOrEmpty(ewsUrl) || string.IsNullOrEmpty(mailboxUpn))
            {
                throw new ArgumentException("EwsUrl and MailboxUpn are required.");
            }

            string folder = string.IsNullOrEmpty(folderName) ? "inbox" : folderName;
            int topCount = top <= 0 || top > 999 ? 25 : top;

            string soapRequest = BuildFindItemRequest(mailboxUpn, folder, topCount, unreadOnly);
            string soapResponse = PostSoap(ewsUrl, soapRequest, domain, username, password);
            List<Dictionary<string, string>> raw = ParseFindItemResponse(soapResponse);

            var rows = new List<MailMessageRow>(raw.Count);
            foreach (Dictionary<string, string> msg in raw)
            {
                DateTime received;
                DateTime.TryParse(GetVal(msg, "DateTimeReceived"), CultureInfo.InvariantCulture, DateTimeStyles.None, out received);

                rows.Add(new MailMessageRow
                {
                    MessageId = Truncate(GetVal(msg, "Id"), 400),
                    Subject = Truncate(GetVal(msg, "Subject"), 1000),
                    FromAddress = Truncate(GetVal(msg, "FromAddress"), 320),
                    FromName = Truncate(GetVal(msg, "FromName"), 200),
                    ReceivedDateTime = received,
                    IsRead = string.Equals(GetVal(msg, "IsRead"), "true", StringComparison.OrdinalIgnoreCase),
                    HasAttachments = string.Equals(GetVal(msg, "HasAttachments"), "true", StringComparison.OrdinalIgnoreCase),
                    BodyPreview = Truncate(GetVal(msg, "Preview"), 4000)
                });
            }
            return rows;
        }

        private static string BuildFindItemRequest(string mailboxUpn, string folder, int top, bool unreadOnly)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<soap:Envelope xmlns:soap=\"").Append(SoapNs).Append("\" xmlns:t=\"").Append(TypesNs).Append("\" xmlns:m=\"").Append(MessagesNs).Append("\">");
            sb.Append("<soap:Header>");
            sb.Append("<t:RequestServerVersion Version=\"Exchange2013_SP1\"/>");
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

        private static string PostSoap(string url, string soapBody, string domain, string username, string password)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(soapBody);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "text/xml; charset=utf-8";
            request.Headers.Add("SOAPAction", "\"http://schemas.microsoft.com/exchange/services/2006/messages/FindItem\"");
            request.ContentLength = bodyBytes.Length;
            request.Timeout = 300000; // 5 minutes -- a scheduled task should fail loudly, not hang forever

            bool hasExplicitCreds = !string.IsNullOrEmpty(username) && password != null;
            if (hasExplicitCreds)
            {
                string domainValue = domain ?? string.Empty;
                var creds = new CredentialCache();
                var netCred = new NetworkCredential(username, password, domainValue);
                var target = new Uri(url);
                creds.Add(target, "NTLM", netCred);
                creds.Add(target, "Negotiate", netCred);
                request.Credentials = creds;
            }
            else
            {
                // Uses whatever account this process is running as (e.g. the Task Scheduler
                // job's "Run as" identity).
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

            XmlNode faultNode = doc.SelectSingleNode("//soap:Fault", nsmgr);
            if (faultNode != null)
            {
                XmlNode faultString = faultNode.SelectSingleNode("faultstring");
                string faultMsg = faultString != null ? faultString.InnerText : faultNode.OuterXml;
                throw new Exception("EWS SOAP fault: " + faultMsg);
            }

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
