//-----------------------------------------------------------------------
// <copyright file="DocumentReplicationResponder.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Linq;
using log4net;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Bundles.Replication.Data;
using Raven.Database;
using Raven.Database.Json;
using Raven.Database.Server.Responders;
using Raven.Database.Storage;
using Raven.Http.Abstractions;
using Raven.Http.Extensions;
using Raven.Json.Linq;

namespace Raven.Bundles.Replication.Reponsders
{
    public class DocumentReplicationResponder : RequestResponder
    {
        private ILog log = LogManager.GetLogger(typeof (DocumentReplicationResponder));

        public override void Respond(IHttpContext context)
        {
            var src = context.Request.QueryString["from"];
            if (string.IsNullOrEmpty(src))
            {
                context.SetStatusToBadRequest();
                return;
            }
            while (src.EndsWith("/"))
                src = src.Substring(0, src.Length - 1);// remove last /, because that has special meaning for Raven
            if (string.IsNullOrEmpty(src))
            {
                context.SetStatusToBadRequest();
                return;
            }
			var array = context.ReadJsonArray();
			using(Database.DisableAllTriggersForCurrentThread())
            {
                Database.TransactionalStorage.Batch(actions =>
                {
                    string lastEtag = Guid.Empty.ToString();
                    foreach (RavenJObject document in array)
                    {
                        var metadata = document.Value<RavenJObject>("@metadata");
                        if(metadata[ReplicationConstants.RavenReplicationSource] == null)
                        {
                            // not sure why, old document from when the user didn't have replciation
                            // that we suddenly decided to replicate, choose the source for that
                            metadata[ReplicationConstants.RavenReplicationSource] = RavenJToken.FromObject(src);
                        }
                        lastEtag = metadata.Value<string>("@etag");
                        var id = metadata.Value<string>("@id");
                        document.Remove("@metadata");
                        ReplicateDocument(actions, id, metadata, document, src);
                    }

                    var replicationDocKey = ReplicationConstants.RavenReplicationSourcesBasePath + "/" + src;
                    var replicationDocument = Database.Get(replicationDocKey, null);
                    var lastAttachmentId = Guid.Empty;
                    if (replicationDocument != null)
                    {
                        lastAttachmentId =
                            replicationDocument.DataAsJson.JsonDeserialization<SourceReplicationInformation>().
                                LastAttachmentEtag;
                    }
                    Database.Put(replicationDocKey, null,
                                 RavenJObject.FromObject(new SourceReplicationInformation
                                 {
                                     LastDocumentEtag = new Guid(lastEtag),
                                     LastAttachmentEtag = lastAttachmentId
                                 }),
                                 new RavenJObject(), null);
                });
            }
        }

        private void ReplicateDocument(IStorageActionsAccessor actions, string id, RavenJObject metadata, RavenJObject document, string src)
        {
            var existingDoc = actions.Documents.DocumentByKey(id, null);
            if (existingDoc == null)
            {
                log.DebugFormat("New document {0} replicated successfully from {1}", id, src);
				actions.Documents.AddDocument(id, Guid.Empty, document, metadata);
                return;
            }
            
            var existingDocumentIsInConflict = existingDoc.Metadata[ReplicationConstants.RavenReplicationConflict] != null;
            if (existingDocumentIsInConflict == false &&                    // if the current document is not in conflict, we can continue without having to keep conflict semantics
                (IsDirectChildOfCurrentDocument(existingDoc, metadata))) // this update is direct child of the existing doc, so we are fine with overwriting this
            {
                log.DebugFormat("Existing document {0} replicated successfully from {1}", id, src);
				actions.Documents.AddDocument(id, null, document, metadata);
                return;
            }


        	var newDocumentConflictId = id + "/conflicts/" +
        	                            metadata.Value<string>(ReplicationConstants.RavenReplicationSource) + "/" +
        	                            metadata.Value<string>("@etag");
            metadata.Add(ReplicationConstants.RavenReplicationConflict, RavenJToken.FromObject(true));
			actions.Documents.AddDocument(newDocumentConflictId, null, document, metadata);

            if (existingDocumentIsInConflict) // the existing document is in conflict
            {
                log.DebugFormat("Conflicted document {0} has a new version from {1}, adding to conflicted documents", id, src);
                
                // just update the current doc with the new conflict document
                existingDoc.DataAsJson.Value<RavenJArray>("Conflicts").Add(RavenJToken.FromObject(newDocumentConflictId));
				actions.Documents.AddDocument(id, existingDoc.Etag, existingDoc.DataAsJson, existingDoc.Metadata);
                return;
            }
            log.DebugFormat("Existing document {0} is in conflict with replicated version from {1}, marking document as conflicted", id, src);
                
            // we have a new conflict
            // move the existing doc to a conflict and create a conflict document
        	var existingDocumentConflictId = id + "/conflicts/" + Database.TransactionalStorage.Id + "/" + existingDoc.Etag;
            
            existingDoc.Metadata.Add(ReplicationConstants.RavenReplicationConflict, RavenJToken.FromObject(true));
			actions.Documents.AddDocument(existingDocumentConflictId, null, existingDoc.DataAsJson, existingDoc.Metadata);
        	actions.Documents.AddDocument(id, null,
        	                              new RavenJObject
        	                              {
        	                              	{
        	                              	"Conflicts", new RavenJArray(existingDocumentConflictId, newDocumentConflictId)
        	                              	}
        	                              },
        	                              new RavenJObject
        	                              {
        	                              	{ReplicationConstants.RavenReplicationConflict, true},
        	                              	{"@Http-Status-Code", 409},
        	                              	{"@Http-Status-Description", "Conflict"}
        	                              });
        }

        private static bool IsDirectChildOfCurrentDocument(JsonDocument existingDoc, RavenJObject metadata)
        {
			var version = new RavenJObject
        	{
        		{ReplicationConstants.RavenReplicationSource, existingDoc.Metadata[ReplicationConstants.RavenReplicationSource]},
        		{ReplicationConstants.RavenReplicationVersion, existingDoc.Metadata[ReplicationConstants.RavenReplicationVersion]},
        	};

			var history = metadata[ReplicationConstants.RavenReplicationHistory];
			if (history == null) // no history, not a parent
				return false;

			return history.Values().Contains(version, new RavenJTokenEqualityComparer());
        }

        public override string UrlPattern
        {
            get { return "^/replication/replicateDocs$"; }
        }

        public override string[] SupportedVerbs
        {
            get { return new[] { "POST" }; }
        }
    }
}
