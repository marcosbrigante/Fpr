﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Fpr.Utils
{
    public class ProjectionExpression<TSource>
    {
        #region Members

        private static readonly Dictionary<int, Expression> _expressionCache = new Dictionary<int, Expression>();

        internal static readonly Dictionary<int, BaseProjectionConfig> ConfigurationCache = new Dictionary<int, BaseProjectionConfig>();

        private readonly IQueryable<TSource> _source;

        #endregion

        public ProjectionExpression(IQueryable<TSource> source)
        {
            _source = source;
        }

        public IQueryable<TDest> To<TDest>()
        {
            var parameterIndexs = new Dictionary<int, int>();

            var queryExpression = BuildExpression<TDest>(parameterIndexs);

            return _source.Select(queryExpression);
        }

        private static Expression<Func<TSource, TDest>> GetCachedExpression<TDest>()
        {
            var key = ReflectionUtils.GetHashKey<TSource, TDest>();

            return _expressionCache.ContainsKey(key) ? _expressionCache[key] as Expression<Func<TSource, TDest>> : null;
        }

        private static BaseProjectionConfig GetCachedConfig(Type sourceType, Type destinationType)
        {
            var key = ReflectionUtils.GetHashKey(sourceType, destinationType);

            return ConfigurationCache.ContainsKey(key) ? ConfigurationCache[key] : null;
        }

        private static Expression<Func<TSource, TDest>> BuildExpression<TDest>(Dictionary<int, int> parameterIndexs, int index = 0)
        {
            var cachedExp = GetCachedExpression<TDest>();
            if (cachedExp != null)
                return cachedExp;

            lock (_expressionCache)
            {
                cachedExp = GetCachedExpression<TDest>();
                if (cachedExp != null)
                    return cachedExp;

                var sourceType = typeof (TSource);
                var destType = typeof (TDest);

                var config = GetCachedConfig(sourceType, destType);
                bool hasConfig = config != null;

                var sourceProperties = sourceType.GetProperties();
                var destinationProperties = destType.GetProperties().Where(dest => dest.CanWrite);

                if (hasConfig)
                    destinationProperties = destinationProperties.Where(dest => !config.IgnoreMembers.Contains(dest.Name));

                var parameterExpression = Expression.Parameter(sourceType, string.Concat("src", sourceType.Name, index));

                var bindings = destinationProperties
                    .Select(destinationProperty => HasCustomExpression(config, destinationProperty) ? 
                        BuildCustomBinding(config, parameterExpression, destinationProperty) : 
                        BuildBinding(parameterExpression, destinationProperty, sourceProperties, config, parameterIndexs, index)
                    )
                    .Where(binding => binding != null);

                var expression = Expression.Lambda<Func<TSource, TDest>>(Expression.MemberInit(Expression.New(destType), bindings), parameterExpression);

                var key = ReflectionUtils.GetHashKey(sourceType, destType);
                _expressionCache.Add(key, expression);
                return expression;
            }
        }

        private static MemberAssignment BuildBinding(Expression parameterExpression, MemberInfo destinationProperty, IEnumerable<PropertyInfo> sourceProperties, BaseProjectionConfig config, Dictionary<int, int> parameterIndexs, int index, string sourcePropertyName = null)
        {
            var destPropertyType = ((PropertyInfo)destinationProperty).PropertyType;

            //if ((destPropertyType.IsClass && destPropertyType != typeof(string)) || (destPropertyType.IsGenericType &&
            //        destPropertyType != typeof(string) && destPropertyType.GetInterfaces().Any(t => t.Name == "IEnumerable")))
            //{
                int propertyHashCode = destinationProperty.GetHashCode();

                if (parameterIndexs.ContainsKey(propertyHashCode))
                {
                    parameterIndexs[propertyHashCode] = parameterIndexs[propertyHashCode] + 1;
                }
                else
                {
                    parameterIndexs.Add(propertyHashCode, 1);
                }

                int maxDepth = 3;

                if (config != null && config.MaxDepth > 0)
                    maxDepth = config.MaxDepth;

                if (parameterIndexs[propertyHashCode] >= maxDepth)
                {
                    return null;
                }
            //}

            if(!string.IsNullOrEmpty(sourcePropertyName))
                parameterExpression = Expression.Property(parameterExpression, parameterExpression.Type.GetProperty(sourcePropertyName));

            var sourceProperty = sourceProperties.FirstOrDefault(src => src.Name == destinationProperty.Name);

            if (sourceProperty != null)
            {
                if (destPropertyType.IsClass && destPropertyType != typeof(string) && destPropertyType != sourceProperty.PropertyType)
                {
                    var sProperties = sourceProperty.PropertyType.GetProperties();
                    var dProperties = destPropertyType.GetProperties().Where(dest => dest.CanWrite);

                    var bindings = dProperties
                                        .Select(dProperty => BuildBinding(parameterExpression, dProperty, sProperties, config, parameterIndexs, index, destinationProperty.Name))
                                        .Where(binding => binding != null);

                    var newMemberExpression = Expression.MemberInit(Expression.New(destPropertyType), bindings);

                    var nullCheck = Expression.Equal(Expression.Property(parameterExpression, sourceProperty), Expression.Constant(null));

                    var convertExp = Expression.Convert(Expression.Constant(null), destPropertyType);

                    var conditionExpression = Expression.Condition(nullCheck, convertExp, newMemberExpression);

                    return Expression.Bind(destinationProperty, conditionExpression);
                }
                
                if (sourceProperty.PropertyType.IsGenericType && destPropertyType.IsGenericType &&
                    destPropertyType != typeof(string) && destPropertyType.GetInterfaces().Any(t => t.Name == "IEnumerable"))
                {
                    var sourceGenericArgument = sourceProperty.PropertyType.GetGenericArguments()[0];
                    var destinationGenericArgument = destPropertyType.GetGenericArguments()[0];

                    var expression = typeof(ProjectionExpression<>).MakeGenericType(sourceGenericArgument)
                        .GetMethod("BuildExpression", BindingFlags.NonPublic | BindingFlags.Static)
                        .MakeGenericMethod(destinationGenericArgument)
                        .Invoke(null, new object[] { parameterIndexs, ++index });

                    MethodCallExpression selectExpression = Expression.Call(
                        typeof(Enumerable),
                        "Select",
                        new[] { sourceGenericArgument, destinationGenericArgument },
                        Expression.Property(parameterExpression, sourceProperty),
                        (Expression)expression);

                    return Expression.Bind(destinationProperty, selectExpression);
                }

                Expression rightExp = Expression.Property(parameterExpression, sourceProperty);

                if (destPropertyType != sourceProperty.PropertyType)
                    rightExp = Expression.Convert(rightExp, destPropertyType);

                return Expression.Bind(destinationProperty, rightExp);
            }
            
            
            sourceProperty = sourceProperties.FirstOrDefault(p => p.PropertyType.IsClass &&
                                                                  p.PropertyType != typeof(string) &&
                                                                  destinationProperty.Name.StartsWith(p.Name));

            if (sourceProperty != null)
            {
                var sourceChildProperty = sourceProperty.PropertyType.GetProperties().FirstOrDefault(src => src.Name == destinationProperty.Name.Substring(sourceProperty.Name.Length));

                if (sourceChildProperty != null)
                {
                    Expression childExp = Expression.Property(Expression.Property(parameterExpression, sourceProperty), sourceChildProperty);

                    var nullCheck = Expression.Equal(Expression.Property(parameterExpression, sourceProperty), Expression.Constant(null));

                    Type destinationPropertyType = ((PropertyInfo)destinationProperty).PropertyType;

                    bool isNullable = destinationPropertyType.IsGenericType && destinationPropertyType.GetGenericTypeDefinition() == typeof(Nullable<>);

                    Expression defaultValueExp;

                    if (!isNullable && destinationPropertyType.IsValueType)
                        defaultValueExp = Expression.Constant(ActivatorExtensions.CreateInstance(destinationPropertyType));
                    else
                        defaultValueExp = Expression.Convert(Expression.Constant(null), destinationPropertyType);

                    if (sourceProperty.PropertyType != destinationPropertyType)
                        childExp = Expression.Convert(childExp, destinationPropertyType);

                    var conditionExpression = Expression.Condition(nullCheck, defaultValueExp, childExp);

                    return Expression.Bind(destinationProperty, conditionExpression);
                }
            }

            return null;
        }


        private static bool HasCustomExpression(BaseProjectionConfig config, PropertyInfo pi)
        {
            return config != null && config.Expressions.Any(c => c.DestinationMemberName == pi.Name);
        }

        private static MemberAssignment BuildCustomBinding(BaseProjectionConfig config, ParameterExpression parameterExpression, PropertyInfo destinationProperty)
        {
            var expression = config.Expressions.FirstOrDefault(c => c.DestinationMemberName == destinationProperty.Name);
            if (expression != null)
            {
                var lambda = expression.SourceExpression as LambdaExpression;
                if (lambda != null)
                {
                    var rightExp = new ParameterRenamer().Rename(lambda.Body, parameterExpression);
                    return Expression.Bind(destinationProperty, rightExp);
                }
            }

            return null;
        }
    }
}
