﻿using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpenDMS.Storage.Providers.CouchDB.Transactions.Processes
{
    public class CheckoutVersion : Base
    {
        private Data.VersionId _id;
        private Security.RequestingPartyType _requestingPartyType;
        private Security.Session _session;
        private Data.Resource _resource;
        private JObject _resourceRemainder;

        public Data.Version Version { get; private set; }
        public JObject Remainder { get; private set; }

        public CheckoutVersion(IDatabase db, Data.VersionId id,
            Security.RequestingPartyType requestingPartyType, Security.Session session, int sendTimeout,
            int receiveTimeout, int sendBufferSize, int receiveBufferSize)
            : base(db, sendTimeout, receiveTimeout, sendBufferSize, receiveBufferSize)
        {
            _id = id;
            _requestingPartyType = requestingPartyType;
            _session = session;
        }

        public override void Process()
        {
            RunTaskProcess(new Tasks.DownloadResource(_db, _id.ResourceId, _sendTimeout, _receiveTimeout,
                    _sendBufferSize, _receiveBufferSize));
        }

        public override void TaskComplete(Tasks.Base sender, ICommandReply reply)
        {
            Type t = sender.GetType();

            if (t == typeof(Tasks.DownloadResource))
            {
                Tasks.DownloadResource task = (Tasks.DownloadResource)sender;
                _resource = task.Resource;
                _resourceRemainder = task.Remainder;
                RunTaskProcess(new Tasks.CheckResourcePermissions(_db, _resource, _requestingPartyType,
                    _session, Security.Authorization.ResourcePermissionType.Checkout, _sendTimeout, _receiveTimeout,
                    _sendBufferSize, _receiveBufferSize));
            }
            else if (t == typeof(Tasks.CheckResourcePermissions))
            {
                Tasks.CheckResourcePermissions task = (Tasks.CheckResourcePermissions)sender;
                if (!task.IsAuthorized)
                {
                    TriggerOnAuthorizationDenied(task);
                    return;
                }
                RunTaskProcess(new Tasks.MarkResourceForCheckout(_resource, _session.User.Username, _sendTimeout, _receiveTimeout,
                    _sendBufferSize, _receiveBufferSize));
            }
            else if (t == typeof(Tasks.MarkResourceForCheckout))
            {
                List<Exception> errors;
                Tasks.MarkResourceForCheckout task = (Tasks.MarkResourceForCheckout)sender;
                Transitions.Resource txResource = new Transitions.Resource();
                Model.Document doc = txResource.Transition(_resource, out errors);
                doc.CombineWith(_resourceRemainder);
                RunTaskProcess(new Tasks.UploadResource(_db, doc, _sendTimeout, _receiveTimeout,
                    _sendBufferSize, _receiveBufferSize));
            }
            else if (t == typeof(Tasks.UploadResource))
            {
                Tasks.UploadResource task = (Tasks.UploadResource)sender;
                if(reply.IsError)
                {
                    TriggerOnError(sender, reply.ToString(), null);
                    return;
                }
                RunTaskProcess(new Tasks.DownloadVersion(_db, _id, _sendTimeout, _receiveTimeout, 
                    _sendBufferSize, _receiveBufferSize));
            }
            else if (t == typeof(Tasks.DownloadVersion))
            {
                Tasks.DownloadVersion task = (Tasks.DownloadVersion)sender;
                Version = task.Version;
                Remainder = task.Remainder;
                TriggerOnComplete(reply, new Tuple<Data.Version, JObject>(Version, Remainder));
            }
            else
            {
                TriggerOnError(sender, reply.ToString(), null);
            }
        }
    }
}
