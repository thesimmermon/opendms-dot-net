﻿using System;

namespace OpenDMS.Storage.Providers.CouchDB.Transactions.Tasks
{
    public class MarkResourceForCheckout : Base
    {
        private Data.Resource _resource;
        private string _username;

        public Data.Resource Resource { get; private set; }

        public MarkResourceForCheckout(Data.Resource resource, string username,
            int sendTimeout, int receiveTimeout, int sendBufferSize, int receiveBufferSize)
            : base(sendTimeout, receiveTimeout, sendBufferSize, receiveBufferSize)
        {
            _resource = resource;
            _username = username;
        }

        public override void Process()
        {
            Resource = _resource;
            Resource.UpdateCheckout(DateTime.Now, _username);
            TriggerOnComplete(null);
        }
    }
}
