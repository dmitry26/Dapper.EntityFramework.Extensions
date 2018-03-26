﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Core.Objects;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Dapper;
using System.Dynamic;
using System.Collections.Concurrent;
using System.Reflection.Emit;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using System.Data.Entity.Core.Metadata.Edm;

namespace Dapper
{
    public static class Extensions
    {
        private static bool _Init = false;
        private static Dictionary<Type, string> _CacheTable = new Dictionary<Type, string>();
        private static string[] AcceptTypes = new string[] { "System.Data.Entity.Spatial.DbGeometry", "System.Data.Entity.Spatial.DbGeography" };

        #region Methods
        public static object Insert<TEntity>(this DbSet<TEntity> source, object entity, bool returnIdentity = true, string propertyKey = "Id")
            where TEntity : class
        {
            Init();
#if DEBUG
            var watch = LogStart("INSERT");
#endif
            var context = source.GetDbContext();
#if DEBUG
            LogElapsed(watch, "GetDbContext");
#endif

            string table = GetTableName<TEntity>(context);
            string[] properties = entity.GetType().GetProperties().Where(o => (o.PropertyType.IsEnum || AcceptTypes.Contains(o.PropertyType.FullName) || Type.GetTypeCode(o.PropertyType) != TypeCode.Object) && o.Name != propertyKey).Select(o => o.Name).ToArray();
            string sql = string.Concat("INSERT INTO ", table, " (",
                string.Join(", ", properties.Select(o => string.Concat("[", o, "]"))),
                ") VALUES(",
                string.Join(", ", properties.Select(o => "@" + o)), ");");

            if (returnIdentity)
                sql += " SELECT SCOPE_IDENTITY()";

#if DEBUG
            LogElapsed(watch, "Parse");
#endif
            var result = context.Database.Connection.ExecuteScalar<object>(sql, entity, transaction: context.Database.CurrentTransaction != null ? context.Database.CurrentTransaction.UnderlyingTransaction : null, commandTimeout: context.Database.CommandTimeout);
#if DEBUG
            LogElapsed(watch, "Execution");
#endif
            if (returnIdentity && result != null)
            {
                var propertyId = entity.GetType().GetProperty(propertyKey);
                if (propertyId != null && propertyId.CanWrite)
                    propertyId.SetValue(entity, Convert.ChangeType(result, propertyId.PropertyType));
            }
            return result;
        }

        public static int Update<TEntity>(this DbSet<TEntity> source, object entity, Expression<Func<TEntity, bool>> where = null, string propertyKey = "Id")
            where TEntity : class
        {
            Init();
#if DEBUG
            var watch = LogStart("UPDATE");
#endif
            var context = source.GetDbContext();
#if DEBUG
            LogElapsed(watch, "GetDbContext");
#endif
            DynamicParameters parameter = new DynamicParameters();
            parameter.AddDynamicParams(entity);
            string table = GetTableName<TEntity>(context);
            Dictionary<string, Type> properties = entity.GetType().GetProperties().ToDictionary(o => o.Name, o => o.PropertyType);
            string sql = string.Concat("UPDATE ", table, " SET ",
                string.Join(", ", properties.Where(o => (o.Value.IsEnum || AcceptTypes.Contains(o.Value.FullName) || Type.GetTypeCode(o.Value) != TypeCode.Object) && o.Key != propertyKey).Select(o => string.Concat("[", o.Key, "]") + " = @" + o.Key)));
#if DEBUG
            LogElapsed(watch, "Parse update");
#endif
            if (where != null)
            {
                sql += " " + ProcessWhere<TEntity>(parameter, source, where);
#if DEBUG
                LogElapsed(watch, "Parse where");
#endif
            }
            else if (properties.Any(o => o.Key == propertyKey))
                sql += string.Concat(" WHERE [", propertyKey, "] = @Id");
            var result =  context.Database.Connection.Execute(sql, parameter, transaction: context.Database.CurrentTransaction != null? context.Database.CurrentTransaction.UnderlyingTransaction : null, commandTimeout: context.Database.CommandTimeout);
#if DEBUG
            LogElapsed(watch, "Execution");
#endif
            return result;
        }

        public static int Delete<TEntity>(this DbSet<TEntity> source, Expression<Func<TEntity, bool>> where = null)
            where TEntity : class
        {
            Init();
#if DEBUG
            var watch = LogStart("DELETE");
#endif
            var context = source.GetDbContext();
#if DEBUG
            LogElapsed(watch, "GetDbContext");
#endif
            DynamicParameters parameter = new DynamicParameters();
            string table = GetTableName<TEntity>(context);
            string sql = string.Concat("DELETE FROM ", table, "");
#if DEBUG
            LogElapsed(watch, "Parse");
#endif
            if (where != null)
            {
                sql += " " + ProcessWhere<TEntity>(parameter, source, where);
#if DEBUG
                LogElapsed(watch, "Parse where");
#endif
            }
            var result = context.Database.Connection.Execute(sql, parameter, transaction: context.Database.CurrentTransaction != null ? context.Database.CurrentTransaction.UnderlyingTransaction : null, commandTimeout: context.Database.CommandTimeout);
#if DEBUG
            LogElapsed(watch, "Execution");
#endif
            return result;
        }

        public static IEnumerable<TEntity> Query<TEntity>(this DbSet<TEntity> source, Expression<Func<TEntity, bool>> where = null, int? top = null, Func<OrderBy<TEntity>, OrderBy<TEntity>> orderBy = null)
            where TEntity : class
        {
            Init();
#if DEBUG
            var watch = LogStart("QUERY");
#endif
            var context = source.GetDbContext();
#if DEBUG
            LogElapsed(watch, "GetDbContext");
#endif

            var objectQuery  = (ObjectQuery<TEntity>)((IObjectContextAdapter)context).ObjectContext.CreateObjectSet<TEntity>();
            if (where != null)
                objectQuery = (ObjectQuery<TEntity>)objectQuery.Where(where);

            if (orderBy != null)
            {
                OrderBy<TEntity> order = new OrderBy<TEntity>(objectQuery);
                orderBy(order);
                objectQuery = order.Query;
            }

            string query = objectQuery.ToTraceString();

            DynamicParameters parameters = new DynamicParameters();
            foreach (var param in objectQuery.Parameters)
                parameters.Add("@" + param.Name, param.Value);

            if (top != null)
            {
                query = "SELECT TOP " + top.Value + query.Substring(query.IndexOf("SELECT") + 6);
            }
#if DEBUG
            LogElapsed(watch, "Parse");
#endif
            var result = context.Database.Connection.Query<TEntity>(query, parameters, transaction: context.Database.CurrentTransaction != null ? context.Database.CurrentTransaction.UnderlyingTransaction : null, commandTimeout: context.Database.CommandTimeout);
#if DEBUG
            LogElapsed(watch, "Execution");
#endif
            return result;
        }

        public static IEnumerable<TResult> Query<TEntity, TResult>(this DbSet<TEntity> source, Expression<Func<TEntity, TResult>> selector, Expression<Func<TEntity, bool>> where = null, int? top = null, Func<OrderBy<TResult>, OrderBy<TResult>> orderBy = null)
            where TEntity : class
            where TResult : class
        {
            Init();
#if DEBUG
            var watch = LogStart("QUERY WITH SELECTOR");
#endif
            var context = source.GetDbContext();
#if DEBUG
            LogElapsed(watch, "GetDbContext");
#endif

            var objectQuery = (ObjectQuery<TEntity>)((IObjectContextAdapter)context).ObjectContext.CreateObjectSet<TEntity>();
            if (where != null)
                objectQuery = (ObjectQuery<TEntity>)objectQuery.Where(where);

            var objectQuerySelector = (ObjectQuery<TResult>)objectQuery.Select(selector);

            if (orderBy != null)
            {
                OrderBy<TResult> order = new OrderBy<TResult>(objectQuerySelector);
                orderBy(order);
                objectQuerySelector = order.Query;
            }

            string query = objectQuerySelector.ToTraceString();

            DynamicParameters parameters = new DynamicParameters();
            foreach (var param in objectQuery.Parameters)
                parameters.Add("@" + param.Name, param.Value);

            if (top != null)
            {
                query = "SELECT TOP " + top.Value + query.Substring(query.IndexOf("SELECT") + 6);
            }
#if DEBUG
            LogElapsed(watch, "Parse");
#endif
            var result = context.Database.Connection.Query<TResult>(query, parameters, transaction: context.Database.CurrentTransaction != null ? context.Database.CurrentTransaction.UnderlyingTransaction : null, commandTimeout: context.Database.CommandTimeout);
#if DEBUG
            LogElapsed(watch, "Execution");
#endif
            return result;
        }

        public static IEnumerable<TEntity> ToDapper<TEntity>(this IQueryable<TEntity> source)
            where TEntity : class
        {
            Init();
#if DEBUG
            var watch = LogStart("ToDapper");
#endif
            var context = source.GetDbContext();
#if DEBUG
            LogElapsed(watch, "GetDbContext");
#endif
            string query = source.ToString();
#if DEBUG
            LogElapsed(watch, "Parse");
#endif
            var result =  context.Database.Connection.Query<TEntity>(query);
#if DEBUG
            LogElapsed(watch, "Execution");
#endif
            return result;
        }

        public static void Initialize(this DbContext context)
        {
#if DEBUG
            var watch = LogStart("Initialize");
#endif
            context.Database.Initialize(true);
#if DEBUG
            LogElapsed(watch, "Database.Initialize");
#endif
            foreach (var property in context.GetType().GetProperties().Where(o => o.PropertyType.Name == "DbSet`1"))
            {
                var dbSet = property.GetValue(context);
                dbSet.ToString();
            }
#if DEBUG
            LogElapsed(watch, "Cache DbSet");
#endif
        }

        public static void Optimization(this DbContext context)
        {
            context.Configuration.AutoDetectChangesEnabled = false;
            context.Configuration.LazyLoadingEnabled = false;
            context.Configuration.ProxyCreationEnabled = false;
            context.Configuration.ValidateOnSaveEnabled = false;
        }
        #endregion

        #region Methods Private

        private static DbContext GetDbContext(this IQueryable source)
        {
            var internalContextProperty = source.Provider.GetType().GetProperty("InternalContext");
            if(internalContextProperty != null)
            {
                var internalContext = internalContextProperty.GetValue(source.Provider);
                var ownerProperty = internalContext.GetType().GetProperty("Owner", BindingFlags.Instance | BindingFlags.Public);
                if (ownerProperty != null)
                {
                    DbContext dbContext = (DbContext)ownerProperty.GetValue(internalContext, null);
                    if (dbContext != null)
                        return dbContext;
                    else
                        throw new Exception("Context not found");
                }
                else
                    throw new Exception("Owner not found");
            }
            else
                throw new Exception("InternalContext not found");
        }

        private static string ProcessWhere<TEntity>(DynamicParameters parameters, DbSet<TEntity> source, System.Linq.Expressions.Expression<Func<TEntity, bool>> where)
            where TEntity : class
        {
            var context = source.GetDbContext();
            var objectQuery = ((ObjectQuery<TEntity>)((IObjectContextAdapter)context).ObjectContext.CreateObjectSet<TEntity>().Where(where));
            string query = objectQuery.ToTraceString();
            foreach(var param in objectQuery.Parameters)
                parameters.Add("@" + param.Name, param.Value);
            return query.Substring(query.IndexOf("WHERE")).Replace("[Extent1].", "");
        }

        private static string GetTableName<TEntity>(DbContext context) where TEntity : class
        {
            var elemType = typeof(TEntity);

            if (!_CacheTable.ContainsKey(elemType))
            {
                string sql = context.Set<TEntity>().ToString();
                var regex = new System.Text.RegularExpressions.Regex("FROM (?<table>.*) AS");
                var match = regex.Match(sql);

                if (!match.Success)
                    throw new Exception(string.Format($"{elemType.Name}: can't determine table name"));

                _CacheTable.Add(elemType,match.Groups["table"].Value);
            }

            return _CacheTable[elemType];
        }

        private static void Init()
        {
            if (!_Init)
            {
                _Init = true;
                Dapper.EntityFramework.Handlers.Register();
            }
        }
        #endregion

#if DEBUG
        private static Stopwatch LogStart(string message)
        {
            Console.WriteLine(message);
            var watch = new Stopwatch();
            watch.Start();
            return watch;
        }

        private static void LogElapsed(Stopwatch watch, string message)
        {
            Console.WriteLine(watch.ElapsedMilliseconds + "ms - " + message);
            watch.Restart();
        }
#endif
    }

    #region Class
    public class OrderBy<TEntity>
        where TEntity : class
    {
        private int _Orders = 0;
        public OrderBy(ObjectQuery<TEntity> query)
        {
            this.Query = query;
        }

        public ObjectQuery<TEntity> Query { get; private set; }

        public OrderBy<TEntity> Asc<TKey>(Expression<Func<TEntity, TKey>> keySelector)
        {
            if (_Orders > 0)
                this.Query = (ObjectQuery<TEntity>)this.Query.ThenBy(keySelector);
            else
                this.Query = (ObjectQuery<TEntity>)this.Query.OrderBy(keySelector);
            _Orders++;
            return this;
        }

        public OrderBy<TEntity> Desc<TKey>(Expression<Func<TEntity, TKey>> keySelector)
        {
            if (_Orders > 0)
                this.Query = (ObjectQuery<TEntity>)this.Query.ThenByDescending(keySelector);
            else
                this.Query = (ObjectQuery<TEntity>)this.Query.OrderByDescending(keySelector);
            _Orders++;
            return this;
        }
    }


    #endregion
}
