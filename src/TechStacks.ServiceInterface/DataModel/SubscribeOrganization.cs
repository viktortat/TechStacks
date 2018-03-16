﻿using System;
using ServiceStack.DataAnnotations;

namespace TechStacks.ServiceInterface.DataModel
{
    public class SubscribeOrganization
    {
        [AutoIncrement]
        public long Id { get; set; }

        public int UserId { get; set; }

        public string UserName { get; set; }

        [Index]
        public int OrganizationId { get; set; }

        [PgSqlTextArray]
        public string[] PostTypes { get; set; }

        public int FrequencyDays { get; set; }

        public long LastSyncedId { get; set; }

        public DateTime? LastSynced { get; set; }

        public DateTime Created { get; set; }
    }
}