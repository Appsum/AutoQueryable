﻿using System.Linq;

namespace AutoQueryable.Core.Models
{
    public class AutoQueryableContext : IAutoQueryableContext
    {
        private readonly IAutoQueryHandler _autoQueryHandler;
        private readonly IAutoQueryableProfile _profile;
        public IQueryable<dynamic> TotalCountQuery { get; private set; }

        public AutoQueryableContext(IAutoQueryableProfile profile, IAutoQueryHandler autoQueryHandler)
        {
            _autoQueryHandler = autoQueryHandler;
            _profile = profile;
        }

        public IQueryable<dynamic> GetAutoQuery<T>(IQueryable<T> query) where T : class
        {
            var result = _autoQueryHandler.GetAutoQuery(query, _profile);
            TotalCountQuery = _autoQueryHandler.TotalCountQuery;
            return result;
        }
    }
}