﻿using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Linq2DynamoDb.DataContext.ExpressionUtils;
using Linq2DynamoDb.DataContext.Utils;

namespace Linq2DynamoDb.DataContext
{
    /// <summary>
    /// Implementation of IQueryProvider. Also does some magic of pre- and post-evaluating LINQ expressions.
    /// </summary>
    public class QueryProvider : IQueryProvider
    {
        private readonly TableDefinitionWrapper _tableWrapper;

        // There should be one static instance of SubtreeEvaluationVisitor per thread, as it should be 
        // able to detect recursions
        static readonly ThreadLocal<SubtreeEvaluationVisitor> SubtreeEvaluator = new ThreadLocal<SubtreeEvaluationVisitor>(() => new SubtreeEvaluationVisitor());

        static readonly ScalarMethodsVisitor ScalarMethodsVisitor = new ScalarMethodsVisitor();

        internal QueryProvider(TableDefinitionWrapper tableWrapper)
        {
            this._tableWrapper = tableWrapper;
        }

        /// <summary>
        /// Evaluates LINQ expression and queries DynamoDb table for results
        /// </summary>
        internal object ExecuteQuery(Expression expression)
        {
            // replacing Count(predicate) with Where(predicate).Count()
            expression = ScalarMethodsVisitor.Visit(expression);

            // pre-executing everything, that can be executed locally
            expression = SubtreeEvaluator.Value.EvaluateSubtree(expression);

            // traversing the expression to find out the type of entities to be returned
            var entityTypeExtractor = new EntityTypeExtractionVisitor();
            entityTypeExtractor.Visit(expression);

            if (entityTypeExtractor.TableEntityType == null)
            {
                throw new InvalidOperationException("Failed to extract the table entity type from the query");
            }

            Type entityType = entityTypeExtractor.EntityType ?? entityTypeExtractor.TableEntityType;

            // translating the query into set of conditions and converting the expression at the same time
            // (all Queryable method calls will be replaced by a param of IQueryable<T> type)
            var visitor = new QueryableMethodsVisitor(entityType, entityTypeExtractor.TableEntityType);
            expression = visitor.Visit(expression);

            // executing get/query/scan operation against DynamoDb table
            var result = this._tableWrapper.LoadEntities(visitor.TranslationResult, entityType);

            // trying to support other (mostly Enumerable's single-entity) operations 
            var enumerableResult = result as IEnumerable;
            if (enumerableResult != null)
            {
                var queryableResult = enumerableResult.AsQueryable();

                var lambda = Expression.Lambda(expression, visitor.EnumerableParameterExp);

                // Here the default methods for IEnumerable<T> are called.
                // This allows to support First(), Last(), Any() etc.
                try
                {
                    result = lambda.Compile().DynamicInvoke(queryableResult);
                }
                catch (TargetInvocationException ex)
                {
                    throw ex.InnerException;
                }
            }
            return result;
        }

        #region IQueryProvider implementation. Copied from IQToolkit

        IQueryable<TEntity> IQueryProvider.CreateQuery<TEntity>(Expression expression)
        {
            return new Query<TEntity>(this, expression);
        }

        IQueryable IQueryProvider.CreateQuery(Expression expression)
        {
            Type elementType = ReflectionUtils.GetElementType(expression.Type);
            try
            {
                return (IQueryable)Activator.CreateInstance(typeof(Query<>).MakeGenericType(elementType), new object[] { this, expression });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>
        /// This method is called by Queryable's SingleValue-methods (First(), Single(), Any() etc.)
        /// </summary>
        TResult IQueryProvider.Execute<TResult>(Expression expression)
        {
            return (TResult)this.ExecuteQuery(expression);
        }

        object IQueryProvider.Execute(Expression expression)
        {
            return this.ExecuteQuery(expression);
        }

        #endregion
    }
}
