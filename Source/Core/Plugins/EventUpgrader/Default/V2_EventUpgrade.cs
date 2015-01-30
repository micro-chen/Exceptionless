﻿using System;
using System.Linq;
using Exceptionless.Core.Pipeline;
using Exceptionless.Core.Extensions;
using Exceptionless.Models.Data;
using Newtonsoft.Json.Linq;
using NLog.Fluent;

namespace Exceptionless.Core.Plugins.EventUpgrader {
    [Priority(2000)]
    public class V2EventUpgrade : IEventUpgraderPlugin {
        public void Upgrade(EventUpgraderContext ctx) {
            if (ctx.Version > new Version(2, 0))
                return;

            foreach (var doc in ctx.Documents.OfType<JObject>()) {
                bool isNotFound = doc.GetPropertyStringValue("Code") == "404";

                if (ctx.IsMigration) {
                    doc.Rename("ErrorStackId", "StackId");
                } else {
                    if (isNotFound)
                        doc.Remove("Id");
                    else
                        doc.RenameOrRemoveIfNullOrEmpty("Id", "ReferenceId");

                    doc.Remove("OrganizationId");
                    doc.Remove("ProjectId");
                    doc.Remove("ErrorStackId");
                }

                doc.RenameOrRemoveIfNullOrEmpty("OccurrenceDate", "Date");
                doc.Remove("ExceptionlessClientInfo");
                if (!doc.RemoveIfNullOrEmpty("Tags")) {
                    var tags = doc.GetValue("Tags");
                    if (tags.Type == JTokenType.Array) {
                        foreach (JToken tag in tags.ToList()) {
                            string t = tag.ToString();
                            if (String.IsNullOrEmpty(t) || t.Length > 255)
                                tag.Remove();
                        }
                    }
                }

                doc.RenameOrRemoveIfNullOrEmpty("RequestInfo", "@request");
                bool hasRequestInfo = doc["@request"] != null;

                if (!isNotFound)
                    doc.RenameOrRemoveIfNullOrEmpty("EnvironmentInfo", "@environment");
                else
                    doc.Remove("EnvironmentInfo");

                doc.RenameAll("ExtendedData", "Data");

                var extendedData = doc.Property("Data") != null ? doc.Property("Data").Value as JObject : null;
                if (extendedData != null) {
                    if (!isNotFound)
                        extendedData.RenameOrRemoveIfNullOrEmpty("TraceLog", "@trace");
                    else
                        extendedData.Remove("TraceLog");
                }

                if (isNotFound && hasRequestInfo) {
                    doc.RemoveAll("Code", "Type", "Message", "Inner", "StackTrace", "TargetMethod", "Modules");
                    if (extendedData != null && extendedData["__ExceptionInfo"] != null)
                        extendedData.Remove("__ExceptionInfo");

                    doc.Add("Type", new JValue("404"));
                } else {
                    var error = new JObject();

                    if (!doc.RemoveIfNullOrEmpty("Message"))
                        error.Add("Message", doc["Message"].Value<string>());

                    error.MoveOrRemoveIfNullOrEmpty(doc, "Code", "Type", "Inner", "StackTrace", "TargetMethod", "Modules");

                    // Copy the exception info from root extended data to the current errors extended data.
                    if (extendedData != null && extendedData["__ExceptionInfo"] != null) {
                        error.Add("Data", new JObject());
                        ((JObject)error["Data"]).MoveOrRemoveIfNullOrEmpty(extendedData, "__ExceptionInfo");
                    }

                    string id = doc["Id"] != null ? doc["Id"].Value<string>(): null;
                    RenameAndValidateExtraExceptionProperties(id, error);

                    var inner = error["Inner"] as JObject;
                    while (inner != null) {
                        RenameAndValidateExtraExceptionProperties(id, inner);
                        inner = inner["Inner"] as JObject;
                    }

                    doc.Add("Type", new JValue(isNotFound ? "404" : "error"));
                    doc.Add("@error", error);
                }

                string emailAddress = doc.GetPropertyStringValueAndRemove("UserEmail");
                string userDescription = doc.GetPropertyStringValueAndRemove("UserDescription");
                if (!String.IsNullOrWhiteSpace(emailAddress) && !String.IsNullOrWhiteSpace(userDescription))
                    doc.Add("@user_description", JObject.FromObject(new UserDescription(emailAddress, userDescription)));

                string identity = doc.GetPropertyStringValueAndRemove("UserName");
                if (!String.IsNullOrWhiteSpace(identity))
                    doc.Add("@user", JObject.FromObject(new UserInfo(identity)));

                doc.RemoveAllIfNullOrEmpty("Data", "GenericArguments", "Parameters");
            }
        }

        private void RenameAndValidateExtraExceptionProperties(string id, JObject error) {
            if (error == null)
                return;

            var extendedData = error["Data"] as JObject;
            if (extendedData == null || extendedData["__ExceptionInfo"] == null)
                return;

            string json = extendedData["__ExceptionInfo"].ToString();
            extendedData.Remove("__ExceptionInfo");

            if (String.IsNullOrWhiteSpace(json))
                return;

            if (json.Length > 200000) {
                Log.Error().Message("Event: {0} __ExceptionInfo is Too Big: {1}", id, json.Length).Write();
                return;
            }

            var ext = new JObject();
            try {
                var extraProperties = JObject.Parse(json);
                foreach (var property in extraProperties.Properties()) {
                    string dataKey = property.Name;
                    if (extendedData[dataKey] != null)
                        dataKey = "_" + dataKey;

                    if (property.IsNullOrEmpty())
                        continue;

                    ext.Add(dataKey, property.Value);
                }
            } catch (Exception) { }

            if (ext.IsNullOrEmpty())
                return;

            extendedData.Add("@ext", ext);
        }
    }
}