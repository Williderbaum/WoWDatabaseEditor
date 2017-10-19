﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shaolinq;

namespace WDE.MySqlDatabase.Models
{
    [DataAccessModel]
    public abstract class TrinityDatabase : DataAccessModel
    {
        [DataAccessObjects]
        public abstract DataAccessObjects<MySqlCreatureTemplate> CreatureTemplate { get; }

        [DataAccessObjects]
        public abstract DataAccessObjects<MySqlSmartScriptLine> SmartScript { get; }

        [DataAccessObjects]
        public abstract DataAccessObjects<MySqlGameObjectTemplate> GameObjectTemplate { get; }

        [DataAccessObjects]
        public abstract DataAccessObjects<MySqlQuestTemplate> QuestTemplate { get; }
    }
}
