﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace WDE.Common.Database
{
    public interface IQuestTemplate
    {
        uint Entry { get; set; }
        string Name { get; set; }
    }
}
