﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using AutoQueryable.Core.Clauses;
using AutoQueryable.Core.CriteriaFilters;
using AutoQueryable.Core.Enums;
using AutoQueryable.Core.Extensions;
using AutoQueryable.Helpers;

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

    public interface IAutoQueryHandler
    {
        IQueryable<dynamic> GetAutoQuery<T>(IQueryable<T> query, IAutoQueryableProfile profile) where T : class;
        IQueryable<dynamic> TotalCountQuery { get; }
    }

    public class AutoQueryHandler : IAutoQueryHandler
    {
        private readonly IQueryStringAccessor _queryStringAccessor;
        private readonly ICriteriaFilterManager _criteriaFilterManager;
        private readonly IClauseMapManager _clauseMapManager;
        private readonly IClauseValueManager _clauseValueManager;
        private readonly IQueryBuilder _queryBuilder;
        public IQueryable<dynamic> TotalCountQuery { get; private set; }
        

        public AutoQueryHandler(IQueryStringAccessor queryStringAccessor, ICriteriaFilterManager criteriaFilterManager, IClauseMapManager clauseMapManager, IClauseValueManager clauseValueManager, IQueryBuilder queryBuilder)
        {
            _queryStringAccessor = queryStringAccessor;
            _criteriaFilterManager = criteriaFilterManager;
            _clauseMapManager = clauseMapManager;
            _clauseValueManager = clauseValueManager;
            _queryBuilder = queryBuilder;
        }

        public IQueryable<dynamic> GetAutoQuery<T>(IQueryable<T> query, IAutoQueryableProfile profile) where T : class
        {
            // Reset the TotalCountQuery
            TotalCountQuery = null;

            // No query string, get only selectable columns
            if (string.IsNullOrEmpty(_queryStringAccessor.QueryString))
            {
                return GetDefaultSelectableQuery(query, profile);
            }

            _getClauses<T>(profile);
            var criterias = profile.IsClauseAllowed(ClauseType.Filter) ? GetCriterias<T>().ToList() : null;
            
            var queryResult = _queryBuilder.Build(query, criterias, profile);

            // If pagination is used, add the totalcountquery to the context to be passed to the ToPagedResultAsync() method after the query
            if(_clauseValueManager.Page != null)
            {
                TotalCountQuery = _queryBuilder.TotalCountQuery;
            }

            return queryResult;
        }
        private void _getClauses<T>(IAutoQueryableProfile profile) where T : class
        {
            // Set the defaults to start with, then fill/overwrite with the query string values
            _clauseValueManager.SetDefaults(typeof(T), profile);
            //var clauses = new List<Clause>();
            foreach (var q in _queryStringAccessor.QueryStringParts.Where(q => !q.IsHandled))
            {
                var clauseQueryFilter = _clauseMapManager.FindClauseQueryFilter(q.Value);
                if(clauseQueryFilter != null)
                {
                    var operandValue = _getOperandValue(q.Value, clauseQueryFilter.Alias);
                    var value = clauseQueryFilter.ParseValue(operandValue, typeof(T), profile);
                    var propertyInfo = _clauseValueManager.GetType().GetProperty(clauseQueryFilter.ClauseType.ToString());
                    if(propertyInfo.PropertyType == typeof(bool))
                    {
                        value = bool.Parse(value.ToString());
                    }

                    propertyInfo.SetValue(_clauseValueManager, value);
                    //clauses.Add(new Clause(clauseQueryFilter.ClauseType, value, clauseQueryFilter.ValueType));
                }
            }

            if (_clauseValueManager.Page != null)
            {
                //this.Logger.Information("Overwriting 'skip' clause value because 'page' is set");
                // Calculate skip from page if page query param was set
                var page = _clauseValueManager.Page;
                _clauseValueManager.Top = _clauseValueManager.Top ?? profile.DefaultToTake;
                _clauseValueManager.Skip = _clauseValueManager.Page * _clauseValueManager.Top;
            }

            if (_clauseValueManager.OrderBy == null && profile.DefaultOrderBy != null)
            {
                _clauseValueManager.OrderBy = profile.DefaultOrderBy;
            }

            if (_clauseValueManager.Select.Count == 0)
            {
                _clauseMapManager.GetClauseQueryFilter(ClauseType.Select).ParseValue("", typeof(T), profile);
            }
        }
        private string _getOperandValue(string q, string clauseAlias) => Regex.Split(q, clauseAlias, RegexOptions.IgnoreCase)[1];

        public IEnumerable<Criteria> GetCriterias<T>() where T : class
        {
            foreach (var qPart in _queryStringAccessor.QueryStringParts.Where(q => !q.IsHandled))
            {
                var q = WebUtility.UrlDecode(qPart.Value);
                var criteria = GetCriteria<T>(q);

                if (criteria != null)
                {
                    yield return criteria;
                }
            }
        }

        private Criteria GetCriteria<T>(string q) where T : class
        {

            var filter = _criteriaFilterManager.FindFilter(q);
            if (filter == null)
            {
                return null;
            }

            var operands = Regex.Split(q, filter.Alias, RegexOptions.IgnoreCase);

            PropertyInfo property = null;
            var columnPath = new List<string>();
            var columns = operands[0].Split('.');
            foreach (var column in columns)
            {
                if (property == null)
                {
                    property = typeof(T).GetProperties().FirstOrDefault(p => p.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    var isCollection = property.PropertyType.GetInterfaces().Any(x => x.Name == "IEnumerable");
                    if (isCollection)
                    {
                        var childType = property.PropertyType.GetGenericArguments()[0];
                        property = childType.GetProperties().FirstOrDefault(p => p.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        property = property.PropertyType.GetProperties().FirstOrDefault(p => p.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

                    }
                }

                if (property == null)
                {
                    return null;
                }
                columnPath.Add(property.Name);
            }
            var criteria = new Criteria
            {
                ColumnPath = columnPath,
                Filter = filter,
                Values = operands[1].Split(',')
            };
            return criteria;
        }

        private IQueryable<dynamic> GetDefaultSelectableQuery<T>(IQueryable<T> query, IAutoQueryableProfile profile) where T : class
        {
            var selectColumns = typeof(T).GetSelectableColumns(profile);
            
            query = query.Take(profile.DefaultToTake);

            if (profile.MaxToTake.HasValue)
            {
                query = query.Take(profile.MaxToTake.Value);
            }
            if (profile.UseBaseType)
            {
                return query.Select(SelectHelper.GetSelector<T, T>(selectColumns, profile));
            }
            return query.Select(SelectHelper.GetSelector<T, object>(selectColumns, profile));

        }
    }
}