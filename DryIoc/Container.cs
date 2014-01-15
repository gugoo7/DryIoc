﻿/*
The MIT License (MIT)

Copyright (c) 2013 Maksim Volkau

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;

namespace DryIoc
{
    /// <summary>
    /// IoC Container. Documentation is available at https://bitbucket.org/dadhi/dryioc.
    /// </summary>
    public class Container : IRegistry, IDisposable
    {
        public Container(Action<IRegistry> setup = null)
        {
            _selfWeakRef = new WeakReference(this);
            _syncRoot = new object();
            _resolutionRules = new ResolutionRules();
            _factories = new Dictionary<Type, FactoriesEntry>();
            _decorators = HashTree<Type, Factory[]>.Empty;
            _currentScope = _singletonScope = new Scope();

            _factoryExprCache = HashTree<int, Expression>.Empty;
            _defaultResolutionCache = HashTree<Type, CompiledFactory>.Empty;
            _keyedResolutionCache = HashTree<Type, HashTree<object, CompiledFactory>>.Empty;
            _store = new IndexedStore(_syncRoot, StoreParameter);

            //_constants = new object[3];
            //// Put reference to container into constants, to support container access inside expression. 
            //// It is common for dynamic scenarios.
            //_constants[CONSTANTS_REGISTRY_WEAKREF_INDEX] = new WeakReference(this);
            //_constants[CONSTANTS_CURRENT_SCOPE_INDEX] = _constants[CONSTANTS_SINGLETON_SCOPE_INDEX] = new Scope();


            (setup ?? DefaultSetup).Invoke(this);
        }

        public static Action<IRegistry> DefaultSetup = ContainerSetup.Default;

        public Container OpenScope()
        {
            return new Container(this);
        }

        public ResolutionRules.ResolveUnregisteredService ResolveUnregisteredFrom(IRegistry registry)
        {
            return ResolutionRules.ForUnregisteredService.Append(
                (request, _) => registry.GetOrAddFactory(request, IfUnresolved.ReturnNull));
        }

        public void Dispose()
        {
            _currentScope.Dispose();
        }

        #region Compiled Factory

        public static readonly ParameterExpression StoreParameter = Expression.Parameter(typeof(IndexedStore), "store");
        public static readonly ParameterExpression ResolutionScopeParameter = Expression.Parameter(typeof(Scope), "resolutionScope");

        //public static readonly ParameterExpression ConstantsParameter = Expression.Parameter(typeof(object[]), "constants");
        //public static readonly int CONSTANTS_REGISTRY_WEAKREF_INDEX = 0;
        //public static readonly int CONSTANTS_SINGLETON_SCOPE_INDEX = 1;
        //public static readonly int CONSTANTS_CURRENT_SCOPE_INDEX = 2;

        //public static readonly Expression RegistryWeakRefExpression = Expression.Convert(
        //    Expression.ArrayIndex(ConstantsParameter, Expression.Constant(CONSTANTS_REGISTRY_WEAKREF_INDEX)),
        //    typeof(WeakReference));

        //public static readonly Expression RegistryExpression = Expression.Convert(
        //    Expression.Property(RegistryWeakRefExpression, "Target"),
        //    typeof(IRegistry));

        //public static readonly Expression SingletonScopeExpression = Expression.Convert(
        //    Expression.ArrayIndex(ConstantsParameter, Expression.Constant(CONSTANTS_SINGLETON_SCOPE_INDEX)),
        //    typeof(Scope));

        //public static readonly Expression CurrentScopeExpression = Expression.Convert(
        //    Expression.ArrayIndex(ConstantsParameter, Expression.Constant(CONSTANTS_CURRENT_SCOPE_INDEX)),
        //    typeof(Scope));

        #endregion

        #region IRegistrator

        public Factory Register(Factory factory, Type serviceType, object serviceKey)
        {
            serviceType.ThrowIfNull();
            factory.ThrowIfNull().ThrowIfCannotBeRegisteredWithServiceType(serviceType);

            lock (_syncRoot)
            {
                if (factory.Setup.Type == FactoryType.Decorator)
                {
                    _decorators = _decorators.AddOrUpdate(serviceType, new[] { factory }, Sugar.Append);
                    return factory;
                }

                var entry = _factories.GetOrAdd(serviceType, NewFactoriesEntry);
                if (serviceKey == null)
                {   // default factories will contain all the factories and LastDefault will just point to the latest, 
                    // for memory saving reasons.
                    if (entry.LastDefaultFactory != null)
                        entry.DefaultFactories = (entry.DefaultFactories
                            ?? HashTree<int, Factory>.Empty.AddOrUpdate(entry.MaxDefaultIndex++, entry.LastDefaultFactory))
                            .AddOrUpdate(entry.MaxDefaultIndex++, factory);
                    entry.LastDefaultFactory = factory;
                }
                else if (serviceKey is int)
                {
                    var index = (int)serviceKey;
                    entry.DefaultFactories = (entry.DefaultFactories ?? HashTree<int, Factory>.Empty).AddOrUpdate(index, factory);
                    entry.MaxDefaultIndex = Math.Max(entry.MaxDefaultIndex, index) + 1;
                }
                else if (serviceKey is string)
                {
                    var name = (string)serviceKey;
                    var named = entry.NamedFactories = entry.NamedFactories ?? new Dictionary<string, Factory>();
                    if (named.ContainsKey(name))
                        Throw.If(true, Error.DUPLICATE_SERVICE_NAME, serviceType, name, named[name].ImplementationType);
                    named.Add(name, factory);
                }
            }

            return factory;
        }

        public bool IsRegistered(Type serviceType, string serviceName)
        {
            return ((IRegistry)this).GetFactoryOrDefault(serviceType.ThrowIfNull(), serviceName) != null;
        }

        private static FactoriesEntry NewFactoriesEntry(Type _)
        {
            return new FactoriesEntry();
        }

        #endregion

        #region IResolver

        object IResolver.ResolveDefault(Type serviceType, IfUnresolved ifUnresolved)
        {
            var compiledFactory =
                _defaultResolutionCache.GetValueOrDefault(serviceType) ??
                ResolveAndCacheFactory(serviceType, ifUnresolved);
            return compiledFactory(_store, resolutionScope: null);
        }

        object IResolver.ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved)
        {
            var entry = _keyedResolutionCache.GetValueOrDefault(serviceType) ?? HashTree<object, CompiledFactory>.Empty;
            var compiledFactory = entry.GetValueOrDefault(serviceKey);
            if (compiledFactory == null)
            {
                var request = Request.Create(serviceType, serviceKey, _store);
                var factory = ((IRegistry)this).GetOrAddFactory(request, ifUnresolved);
                if (factory == null) return null;
                compiledFactory = factory.GetExpression().CompileToFactory();
                Interlocked.Exchange(ref _keyedResolutionCache,
                    _keyedResolutionCache.AddOrUpdate(serviceType, entry.AddOrUpdate(serviceKey, compiledFactory)));
            }

            return compiledFactory(_store, resolutionScope: null);
        }

        private CompiledFactory ResolveAndCacheFactory(Type serviceType, IfUnresolved ifUnresolved)
        {
            var request = Request.Create(serviceType, store: _store);
            var factory = ((IRegistry)this).GetOrAddFactory(request, ifUnresolved);
            if (factory == null)
                return EmptyCompiledFactory;
            var compiledFactory = factory.GetExpression().CompileToFactory();
            Interlocked.Exchange(ref _defaultResolutionCache,
                _defaultResolutionCache.AddOrUpdate(serviceType, compiledFactory));
            return compiledFactory;
        }

        private static object EmptyCompiledFactory(IndexedStore store, Scope resolutionScope) { return null; }

        #endregion

        #region IRegistry

        public WeakReference SelfWeakRef { get { return _selfWeakRef; } }
        public ResolutionRules ResolutionRules { get { return _resolutionRules; } }
        public Scope CurrentScope { get { return _currentScope; } }
        public Scope SingletonScope { get { return _singletonScope; } }

        FactoryContext IRegistry.GetOrAddFactory(Request request, IfUnresolved ifUnresolved)
        {
            Factory factory = null;
            var serviceType = request.ServiceType;
            var serviceKey = request.ServiceKey;

            lock (_syncRoot)
            {
                FactoriesEntry entry;
                if (_factories.TryGetValue(serviceType, out entry) &&
                    entry.TryGet(out factory, serviceType, serviceKey, ResolutionRules.GetSingleRegisteredFactory))
                {
                    if (factory.ProvidesFactoryPerRequest)
                        factory = factory.GetFactoryPerRequestOrDefault(request, this);
                }

                if (factory == null && request.OpenGenericServiceType != null &&
                    _factories.TryGetValue(request.OpenGenericServiceType, out entry))
                {
                    Factory openGenericFactory;
                    if (entry.TryGet(out openGenericFactory, serviceType, serviceKey, ResolutionRules.GetSingleRegisteredFactory) ||
                        serviceKey != null && // OR try find generic-wrapper by ignoring service key.
                        entry.TryGet(out openGenericFactory, serviceType, null, ResolutionRules.GetSingleRegisteredFactory) &&
                        openGenericFactory.Setup.Type == FactoryType.GenericWrapper &&
                        openGenericFactory.ProvidesFactoryPerRequest)
                    {
                        factory = openGenericFactory.GetFactoryPerRequestOrDefault(request, this);
                        if (factory != null)
                            Register(factory, serviceType, serviceKey);
                    }
                }
            }

            if (factory == null)
            {
                var factoryContext = ResolutionRules.ForUnregisteredService.Invoke(r => r(request, this));
                if (factoryContext != null)
                    Register(factoryContext.Factory, serviceType, serviceKey);
                else
                    Throw.If(ifUnresolved == IfUnresolved.Throw, Error.UNABLE_TO_RESOLVE_SERVICE, request);
                return factoryContext;
            }

            return factory.With(request, this);
        }

        Expression IRegistry.GetDecoratorExpressionOrDefault(Request request, IRegistry registry)
        {
            // Decorators for non service types are not supported.
            if (request.FactoryType != FactoryType.Service)
                return null;

            // We are already resolving decorator for the service, so stop now.
            var parent = request.GetNonWrapperParentOrDefault();
            if (parent != null && parent.DecoratedFactoryID == request.FactoryID)
                return null;

            var serviceType = request.ServiceType;
            var decoratorFuncType = typeof(Func<,>).MakeGenericType(serviceType, serviceType);

            LambdaExpression resultFuncDecorator = null;
            var funcDecorators = _decorators.GetValueOrDefault(decoratorFuncType);
            if (funcDecorators != null)
            {
                var decoratorRequest = request.MakeDecorated();
                for (var i = 0; i < funcDecorators.Length; i++)
                {
                    var decorator = funcDecorators[i];
                    if (((DecoratorSetup)decorator.Setup).IsApplicable(request))
                    {
                        var newDecorator = decorator.With(decoratorRequest, this).GetExpression();
                        if (resultFuncDecorator == null)
                        {
                            var decorated = Expression.Parameter(serviceType, "decorated");
                            resultFuncDecorator = Expression.Lambda(Expression.Invoke(newDecorator, decorated), decorated);
                        }
                        else
                        {
                            var decorateDecorator = Expression.Invoke(newDecorator, resultFuncDecorator.Body);
                            resultFuncDecorator = Expression.Lambda(decorateDecorator, resultFuncDecorator.Parameters[0]);
                        }
                    }
                }
            }

            var decorators = _decorators.GetValueOrDefault(serviceType);
            var openGenericDecoratorIndex = decorators == null ? 0 : decorators.Length;
            if (request.OpenGenericServiceType != null)
                decorators = decorators.Append(_decorators.GetValueOrDefault(request.OpenGenericServiceType));

            Expression resultDecorator = resultFuncDecorator;
            if (decorators != null)
            {
                var decoratorRequest = request.MakeDecorated();
                for (var i = 0; i < decorators.Length; i++)
                {
                    var decorator = decorators[i];
                    if (((DecoratorSetup)decorator.Setup).IsApplicable(request))
                    {
                        // Cache closed generic registration produced by open-generic decorator.
                        if (i >= openGenericDecoratorIndex && decorator.ProvidesFactoryPerRequest)
                            decorator = Register(decorator.GetFactoryPerRequestOrDefault(request, this), serviceType, null);

                        var decoratorExpr = registry.GetCachedFactoryExpression(decorator.ID);
                        if (decoratorExpr == null)
                        {
                            IList<Type> unusedFunArgs;
                            var funcExpr = decorator.With(decoratorRequest, this)
                                .GetFuncWithArgsOrDefault(decoratorFuncType, out unusedFunArgs)
                                .ThrowIfNull(Error.DECORATOR_FACTORY_SHOULD_SUPPORT_FUNC_RESOLUTION, decoratorFuncType);

                            decoratorExpr = unusedFunArgs != null ? funcExpr.Body : funcExpr;
                            registry.CacheFactoryExpression(decorator.ID, decoratorExpr);
                        }

                        if (resultDecorator == null || !(decoratorExpr is LambdaExpression))
                            resultDecorator = decoratorExpr;
                        else
                        {
                            if (!(resultDecorator is LambdaExpression))
                                resultDecorator = Expression.Invoke(decoratorExpr, resultDecorator);
                            else
                            {
                                var prevDecorators = ((LambdaExpression)resultDecorator);
                                var decorateDecorator = Expression.Invoke(decoratorExpr, prevDecorators.Body);
                                resultDecorator = Expression.Lambda(decorateDecorator, prevDecorators.Parameters[0]);
                            }
                        }
                    }
                }
            }

            return resultDecorator;
        }

        IEnumerable<object> IRegistry.GetKeys(Type serviceType, Func<Factory, bool> condition)
        {
            condition = condition ?? AlwaysTrue;
            lock (_syncRoot)
            {
                FactoriesEntry entry;
                if (TryFindEntry(out entry, serviceType))
                {
                    if (entry.DefaultFactories != null)
                    {
                        foreach (var item in entry.DefaultFactories.TraverseInOrder())
                            if (condition(item.Value))
                                yield return item.Key;
                    }
                    else if (entry.LastDefaultFactory != null &&
                        condition(entry.LastDefaultFactory))
                        yield return 0;

                    if (entry.NamedFactories != null)
                    {
                        foreach (var pair in entry.NamedFactories)
                            if (condition(pair.Value))
                                yield return pair.Key;
                    }
                }
            }
        }

        Factory IRegistry.GetFactoryOrDefault(Type serviceType, object serviceKey)
        {
            lock (_syncRoot)
            {
                FactoriesEntry entry;
                Factory factory;
                if (TryFindEntry(out entry, serviceType) &&
                    entry.TryGet(out factory, serviceType, serviceKey, ResolutionRules.GetSingleRegisteredFactory))
                    return factory;
                return null;
            }
        }

        Type IRegistry.GetWrappedServiceTypeOrSelf(Type serviceType)
        {
            if (!serviceType.IsGenericType)
                return serviceType;

            var factory = ((IRegistry)this).GetFactoryOrDefault(serviceType.GetGenericTypeDefinition(), null);
            if (factory == null || factory.Setup.Type != FactoryType.GenericWrapper)
                return serviceType;

            var wrapperSetup = ((GenericWrapperSetup)factory.Setup);
            var wrappedType = wrapperSetup.GetWrappedServiceType(serviceType.GetGenericArguments());
            return wrappedType == serviceType ? serviceType
                : ((IRegistry)this).GetWrappedServiceTypeOrSelf(wrappedType); // unwrap recursively.
        }

        public Expression GetCachedFactoryExpression(int factoryID)
        {
            return _factoryExprCache.GetValueOrDefault(factoryID);
        }

        public void CacheFactoryExpression(int factoryID, Expression result)
        {
            Interlocked.Exchange(ref _factoryExprCache, _factoryExprCache.AddOrUpdate(factoryID, result));
        }

        private bool TryFindEntry(out FactoriesEntry entry, Type serviceType)
        {
            return _factories.TryGetValue(serviceType, out entry) ||
                serviceType.IsGenericType && !serviceType.IsGenericTypeDefinition &&
                _factories.TryGetValue(serviceType.GetGenericTypeDefinition().ThrowIfNull(), out entry);
        }

        private static bool AlwaysTrue<T>(T _) { return true; }

        #endregion

        #region Internal State

        private readonly WeakReference _selfWeakRef;
        private readonly object _syncRoot;
        private readonly ResolutionRules _resolutionRules;
        private readonly Dictionary<Type, FactoriesEntry> _factories;
        private HashTree<Type, Factory[]> _decorators;
        private readonly Scope _currentScope;
        private readonly Scope _singletonScope;

        private HashTree<int, Expression> _factoryExprCache;
        private HashTree<Type, CompiledFactory> _defaultResolutionCache;
        private HashTree<Type, HashTree<object, CompiledFactory>> _keyedResolutionCache;
        private readonly IndexedStore _store;

        #endregion

        #region Implementation

        // Creates child container with singleton scope, constants and cache shared with parent. BUT with new CurrentScope.
        private Container(Container parent)
        {
            _selfWeakRef = parent._selfWeakRef;
            _syncRoot = parent._syncRoot;
            _resolutionRules = parent.ResolutionRules;
            _factories = parent._factories;
            _decorators = parent._decorators; // TODO: Fix reference to decorators will be overwritten with next registration
            _singletonScope = parent._singletonScope;
            _currentScope = new Scope();

            //var parentConstants = parent._constants;
            //_constants = new object[parentConstants.Length];
            //Array.Copy(parentConstants, 0, _constants, 0, parentConstants.Length);
            //_constants[CONSTANTS_REGISTRY_WEAKREF_INDEX] = new WeakReference(this);
            //_constants[CONSTANTS_CURRENT_SCOPE_INDEX] = new Scope();

            _factoryExprCache = HashTree<int, Expression>.Empty;
            _defaultResolutionCache = HashTree<Type, CompiledFactory>.Empty;
            _keyedResolutionCache = HashTree<Type, HashTree<object, CompiledFactory>>.Empty;
            _store = new IndexedStore(_syncRoot, StoreParameter);
        }

        private sealed class FactoriesEntry
        {
            public Factory LastDefaultFactory;
            public HashTree<int, Factory> DefaultFactories;
            public int MaxDefaultIndex;
            public Dictionary<string, Factory> NamedFactories;

            public bool TryGet(out Factory result, Type serviceType, object serviceKey,
                Func<IEnumerable<Factory>, Factory> getSingleFactory = null)
            {
                result = null;
                if (serviceKey == null)
                {
                    if (DefaultFactories == null)
                        result = LastDefaultFactory;
                    else
                    {
                        var factories = DefaultFactories.TraverseInOrder().Select(_ => _.Value);
                        result = getSingleFactory
                            .ThrowIfNull(Error.EXPECTED_SINGLE_DEFAULT_FACTORY, serviceType,
                                factories.Select(_ => _.ImplementationType))
                            .Invoke(factories);
                    }
                }
                else
                {
                    if (serviceKey is string)
                    {
                        if (NamedFactories != null)
                            NamedFactories.TryGetValue((string)serviceKey, out result);
                    }
                    else if (serviceKey is int)
                    {
                        var index = (int)serviceKey;
                        if (DefaultFactories == null && index == 0)
                            result = LastDefaultFactory;
                        else if (DefaultFactories != null)
                            result = DefaultFactories.GetValueOrDefault(index);
                    }
                }

                return result != null;
            }
        }

        #endregion
    }

    public sealed class IndexedStore
    {
        public IndexedStore(object syncRoot, ParameterExpression storeParameterExpr)
        {
            _syncRoot = syncRoot;
            _storeParameterExpr = storeParameterExpr;
        }

        private static readonly MethodInfo _getMethod = typeof(IndexedStore).GetMethod("Get");
        public object Get(int i)
        {
            return _items[i];
        }

        public int GetOrAdd(object item)
        {
            var index = Array.IndexOf(_items, item);
            if (index != -1)
                return index;

            lock (_syncRoot)
            {
                _items = _items.AppendOrUpdate(item);
                return _items.Length - 1;
            }
        }

        public Expression GetItemExpression(object item, Type itemType)
        {
            var itemIndexExpr = Expression.Constant(GetOrAdd(item), typeof(int));
            var itemExpr = Expression.Call(_storeParameterExpr, _getMethod, itemIndexExpr);
            return Expression.Convert(itemExpr, itemType);
        }

        public Expression GetItemExpression<T>(T item)
        {
            return GetItemExpression(item, typeof(T));
        }

        private object[] _items = { };
        private readonly object _syncRoot;
        private readonly ParameterExpression _storeParameterExpr;
    }

    public delegate object CompiledFactory(IndexedStore store, Scope resolutionScope);

    public static partial class FactoryCompiler
    {
        public static Expression<CompiledFactory> ToCompiledFactoryExpression(this Expression expression)
        {
            // Removing not required Convert from expression root, because CompiledFactory result still be converted at the end.
            if (expression.NodeType == ExpressionType.Convert)
                expression = ((UnaryExpression)expression).Operand;
            return Expression.Lambda<CompiledFactory>(expression, Container.StoreParameter, Container.ResolutionScopeParameter);
        }

        public static CompiledFactory CompileToFactory(this Expression expression)
        {
            var factoryExpression = expression.ToCompiledFactoryExpression();
            CompiledFactory factory = null;
            CompileToMethod(factoryExpression, ref factory);
            // ReSharper disable ConstantNullCoalescingCondition
            return factory ?? factoryExpression.Compile();
            // ReSharper restore ConstantNullCoalescingCondition
        }

        // Partial method definition to be implemented in .NET40 version of Container.
        // It is optional and fine to be not implemented.
        static partial void CompileToMethod(Expression<CompiledFactory> factoryExpression, ref CompiledFactory resultFactory);
    }

    public static class ContainerSetup
    {
        public static Type[] FuncTypes = { typeof(Func<>), typeof(Func<,>), typeof(Func<,,>), typeof(Func<,,,>), typeof(Func<,,,,>) };

        public static void Minimal(IRegistry registry) { }

        public static void Default(IRegistry registry)
        {
            registry.ResolutionRules.ForUnregisteredService.Append(ResolveEnumerableAsStaticArray);

            var manyFactory = new FactoryProvider(
                (_, __) => new DelegateFactory(GetManyExpression),
                ServiceSetup.With(FactoryCachePolicy.ShouldNotCacheExpression));
            registry.Register(typeof(Many<>), manyFactory);

            var funcFactory = new FactoryProvider(
                (_, __) => new DelegateFactory(GetFuncExpression),
                GenericWrapperSetup.With(t => t[t.Length - 1]));
            foreach (var funcType in FuncTypes)
                registry.Register(funcType, funcFactory);

            var lazyFactory = new ReflectionFactory(typeof(Lazy<>),
                getConstructor: t => t.GetConstructor(new[] { typeof(Func<>).MakeGenericType(t.GetGenericArguments()) }),
                setup: GenericWrapperSetup.Default);
            registry.Register(typeof(Lazy<>), lazyFactory);

            var metaFactory = new FactoryProvider(GetMetaFactoryOrDefault, GenericWrapperSetup.With(t => t[0]));
            registry.Register(typeof(Meta<,>), metaFactory);

            var debugExprFactory = new FactoryProvider(
                (_, __) => new DelegateFactory(GetDebugExpression),
                GenericWrapperSetup.Default);
            registry.Register(typeof(DebugExpression<>), debugExprFactory);
        }

        public static ResolutionRules.ResolveUnregisteredService ResolveEnumerableAsStaticArray = (req, reg) =>
        {
            if (!req.ServiceType.IsArray && req.OpenGenericServiceType != typeof(IEnumerable<>))
                return null;

            return new FactoryContext(req, reg, new DelegateFactory((request, registry) =>
            {
                var collectionType = request.ServiceType;

                var itemType = collectionType.IsArray
                    ? collectionType.GetElementType()
                    : collectionType.GetGenericArguments()[0];

                var wrappedItemType = registry.GetWrappedServiceTypeOrSelf(itemType);

                // Composite pattern support: filter out composite root from available keys.
                var parent = request.GetNonWrapperParentOrDefault();
                Func<Factory, bool> condition = null;
                if (parent != null && parent.ServiceType == wrappedItemType)
                    condition = factory => factory.ID != parent.FactoryID;
                var itemKeys = registry.GetKeys(wrappedItemType, condition).ToArray();
                Throw.If(itemKeys.Length == 0, Error.UNABLE_TO_FIND_REGISTERED_ENUMERABLE_ITEMS, wrappedItemType, request);

                var itemExpressions = new List<Expression>(itemKeys.Length);
                foreach (var itemKey in itemKeys)
                {
                    var itemRequest = request.Push(itemType, itemKey);
                    var itemFactory = registry.GetOrAddFactory(itemRequest, IfUnresolved.ReturnNull);
                    if (itemFactory != null)
                        itemExpressions.Add(itemFactory.GetExpression());
                }

                Throw.If(itemExpressions.Count == 0, Error.UNABLE_TO_RESOLVE_ENUMERABLE_ITEMS, itemType, request);
                var newArrayExpr = Expression.NewArrayInit(itemType.ThrowIfNull(), itemExpressions);
                return newArrayExpr;
            }));
        };

        public static Expression GetManyExpression(Request request, IRegistry registry)
        {
            var dynamicEnumerableType = request.ServiceType;
            var itemType = dynamicEnumerableType.GetGenericArguments()[0];

            var wrappedItemType = registry.GetWrappedServiceTypeOrSelf(itemType);

            // Composite pattern support: filter out composite root from available keys.
            var parentFactoryID = -1;
            var parent = request.GetNonWrapperParentOrDefault();
            if (parent != null && parent.ServiceType == wrappedItemType)
                parentFactoryID = parent.FactoryID;

            var resolveMethod = _resolveManyDynamicallyMethod.MakeGenericMethod(itemType, wrappedItemType);
            
            var registryExpr = request.Store.GetItemExpression(registry);
            var weakRefCtor = typeof(WeakReference).GetConstructor(new[] { typeof(object) }).ThrowIfNull();
            var registryRefExpr = Expression.New(weakRefCtor, registryExpr);
            
            var resolveCallExpr = Expression.Call(resolveMethod, registryRefExpr, Expression.Constant(parentFactoryID));

            return Expression.New(dynamicEnumerableType.GetConstructors()[0], resolveCallExpr);
        }

        public static Expression GetFuncExpression(Request request, IRegistry registry)
        {
            var funcType = request.ServiceType;
            var funcTypeArgs = funcType.GetGenericArguments();
            var serviceType = funcTypeArgs[funcTypeArgs.Length - 1];

            var serviceRequest = request.PushPreservingParentKey(serviceType);
            var serviceFactory = registry.GetOrAddFactory(serviceRequest, IfUnresolved.Throw);

            if (funcTypeArgs.Length == 1)
                return Expression.Lambda(funcType, serviceFactory.GetExpression(), null);

            IList<Type> unusedFuncArgs;
            var funcExpr = serviceFactory.GetFuncWithArgsOrDefault(funcType, out unusedFuncArgs)
                .ThrowIfNull(Error.UNSUPPORTED_FUNC_WITH_ARGS, funcType, serviceRequest)
                .ThrowIf(unusedFuncArgs != null, Error.SOME_FUNC_PARAMS_ARE_UNUSED, unusedFuncArgs, request);
            return funcExpr;
        }

        public static Expression GetDebugExpression(Request request, IRegistry registry)
        {
            var ctor = request.ServiceType.GetConstructors()[0];
            var serviceType = request.ServiceType.GetGenericArguments()[0];
            var serviceRequest = request.PushPreservingParentKey(serviceType);

            var factoryExpr = registry.GetOrAddFactory(serviceRequest, IfUnresolved.Throw)
                .GetExpression().ToCompiledFactoryExpression();

            var factoryItemExpr = request.Store.GetItemExpression(factoryExpr);
            return Expression.New(ctor, factoryItemExpr);
        }

        public static Factory GetMetaFactoryOrDefault(Request request, IRegistry registry)
        {
            var genericArgs = request.ServiceType.GetGenericArguments();
            var serviceType = genericArgs[0];
            var metadataType = genericArgs[1];

            var wrappedServiceType = registry.GetWrappedServiceTypeOrSelf(serviceType);
            object resultMetadata = null;
            var serviceKey = request.ServiceKey;
            if (serviceKey == null)
            {
                var resultKey = registry.GetKeys(wrappedServiceType, factory =>
                    (resultMetadata = GetTypedMetadataOrDefault(factory, metadataType)) != null).FirstOrDefault();
                if (resultKey != null)
                    serviceKey = resultKey;
            }
            else
            {
                var factory = registry.GetFactoryOrDefault(wrappedServiceType, serviceKey);
                if (factory != null)
                    resultMetadata = GetTypedMetadataOrDefault(factory, metadataType);
            }

            if (resultMetadata == null)
                return null;

            return new DelegateFactory((req, __) =>
            {
                var serviceRequest = request.Push(serviceType, serviceKey);
                var serviceFactory = registry.GetOrAddFactory(serviceRequest, IfUnresolved.Throw);
                var metaCtor = request.ServiceType.GetConstructors()[0];
                var serviceExpr = serviceFactory.GetExpression();
                var metadataExpr = req.Store.GetItemExpression(resultMetadata, metadataType);
                return Expression.New(metaCtor, serviceExpr, metadataExpr);
            });
        }

        #region Implementation

        private static object GetTypedMetadataOrDefault(Factory factory, Type metadataType)
        {
            var metadata = factory.Setup.Metadata;
            return metadata != null && metadataType.IsInstanceOfType(metadata) ? metadata : null;
        }

        private static readonly MethodInfo _resolveManyDynamicallyMethod =
            typeof(ContainerSetup).GetMethod("ResolveManyDynamically", BindingFlags.Static | BindingFlags.NonPublic);

        internal static IEnumerable<TItem> ResolveManyDynamically<TItem, TWrappedItem>(
            WeakReference registryRef, int parentFactoryID)
        {
            var itemType = typeof(TItem);
            var wrappedItemType = typeof(TWrappedItem);
            var registry = (registryRef.Target as IRegistry).ThrowIfNull(Error.CONTAINER_IS_GARBAGE_COLLECTED);

            var itemKeys = registry.GetKeys(wrappedItemType,
                parentFactoryID == -1 ? (Func<Factory, bool>)null : factory => factory.ID != parentFactoryID);

            foreach (var itemKey in itemKeys)
            {
                var item = registry.ResolveKeyed(itemType, itemKey, IfUnresolved.ReturnNull);
                if (item != null) // skip unresolved items
                    yield return (TItem)item;
            }
        }

        #endregion
    }

    public sealed class ResolutionRules
    {
        public Func<IEnumerable<Factory>, Factory> GetSingleRegisteredFactory;

        public delegate FactoryContext ResolveUnregisteredService(Request request, IRegistry registry);
        public Rules<ResolveUnregisteredService> ForUnregisteredService = new Rules<ResolveUnregisteredService>();

        public delegate object ResolveConstructorParameterServiceKey(ParameterInfo parameter, Request parent, IRegistry registry);
        public Rules<ResolveConstructorParameterServiceKey> ForConstructorParameter = new Rules<ResolveConstructorParameterServiceKey>();

        public delegate bool ResolvePropertyOrFieldServiceKey(out object key, MemberInfo member, Request parent, IRegistry registry);
        public Rules<ResolvePropertyOrFieldServiceKey> ForPropertyOrField = new Rules<ResolvePropertyOrFieldServiceKey>();

        public sealed class Rules<TRule>
        {
            public IEnumerable<TRule> List
            {
                get { return _list ?? Enumerable.Empty<TRule>(); }
                set { Interlocked.Exchange(ref _list, value.ToArray()); }
            }

            public bool IsEmpty
            {
                get
                {
                    var list = _list;
                    return list == null || list.Length == 0;
                }
            }

            public R Invoke<R>(Func<TRule, R> invoke)
            {
                var result = default(R);
                var rules = _list;
                if (rules != null)
                    for (var i = 0; i < rules.Length && Equals(result, default(R)); i++)
                        result = invoke(rules[i]);
                return result;
            }

            public TRule Append(TRule rule)
            {
                Interlocked.Exchange(ref _list, (_list ?? new TRule[0]).AppendOrUpdate(rule));
                return rule;
            }

            public void Remove(TRule rule)
            {
                List = List.Except(new[] { rule });
            }

            private TRule[] _list;
        }
    }

    public static class Error
    {
        public static readonly string UNABLE_TO_RESOLVE_SERVICE =
@"Unable to resolve service {0}. 
Please register service OR adjust resolution rules.";

        public static readonly string UNSUPPORTED_FUNC_WITH_ARGS =
            "Unsupported resolution as {0} of {1}.";

        public static readonly string EXPECTED_IMPL_TYPE_ASSIGNABLE_TO_SERVICE_TYPE =
            "Expecting implementation type {0} to be assignable to service type {1} but it is not.";

        public static readonly string UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE =
            "Unable to register open-generic implementation {0} with non-generic service {1}.";

        public static readonly string UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_CAUSE_SERVICE_DOES_NOT_SPECIFY_ALL_TYPE_ARGS =
            "Unable to register open-generic implementation {0} because service {1} should specify all of its type arguments, but specifies only {2}.";

        public static readonly string USUPPORTED_REGISTRATION_OF_NON_GENERIC_IMPL_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS =
@"Unsupported registration of implementation {0} which is not a generic type definition but contains generic parameters. 
Consider to register generic type definition {1} instead.";

        public static readonly string USUPPORTED_REGISTRATION_OF_NON_GENERIC_SERVICE_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS =
@"Unsupported registration of service {0} which is not a generic type definition but contains generic parameters. 
Consider to register generic type definition {1} instead.";

        public static readonly string EXPECTED_SINGLE_DEFAULT_FACTORY =
@"Expecting single default registration of {0} but found many:
{1}.";

        public static readonly string EXPECTED_NON_ABSTRACT_IMPL_TYPE =
            "Expecting not abstract and not interface implementation type, but found {0}.";

        public static readonly string NO_PUBLIC_CONSTRUCTOR_DEFINED =
            "There is no public constructor defined for {0}.";

        public static readonly string CONSTRUCTOR_MISSES_SOME_PARAMETERS =
            "Constructor [{0}] of {1} misses some arguments required for {2} dependency.";

        public static readonly string UNABLE_TO_SELECT_CONSTRUCTOR =
@"Unable to select single constructor from {0} available in {1}.
Please provide constructor selector when registering service.";

        public static readonly string EXPECTED_FUNC_WITH_MULTIPLE_ARGS =
            "Expecting Func with one or more arguments but found {0}.";

        public static readonly string EXPECTED_CLOSED_GENERIC_SERVICE_TYPE =
            "Expecting closed-generic service type but found {0}.";

        public static readonly string RECURSIVE_DEPENDENCY_DETECTED =
            "Recursive dependency is detected in resolution of:\n{0}.";

        public static readonly string SCOPE_IS_DISPOSED =
            "Scope is disposed and all in-scope instances are no longer available.";

        public static readonly string CONTAINER_IS_GARBAGE_COLLECTED =
            "Container is no longer available (has been garbage-collected already).";

        public static readonly string DUPLICATE_SERVICE_NAME =
            "Service {0} with duplicate name '{1}' is already registered with implementation {2}.";

        public static readonly string GENERIC_WRAPPER_EXPECTS_SINGLE_TYPE_ARG_BY_DEFAULT =
@"Generic Wrapper is working with single service type only, but found many: 
{0}.
Please specify service type selector in Generic Wrapper setup upon registration.";

        public static readonly string SOME_FUNC_PARAMS_ARE_UNUSED =
@"Found some unused Func parameters: 
{0} 
when resolving {1}.";

        public static readonly string DECORATOR_FACTORY_SHOULD_SUPPORT_FUNC_RESOLUTION =
            "Decorator factory should support resolution as {0}, but it does not.";

        public static readonly string UNABLE_TO_FIND_REGISTERED_ENUMERABLE_ITEMS =
            "Unable to find registered services of item type (unwrapped) {0} when resolving {1}.";

        public static readonly string UNABLE_TO_RESOLVE_ENUMERABLE_ITEMS =
            "Unable to resolve any service of item type {0} when resolving {1}.";

        public static readonly string DELEGATE_FACTORY_EXPRESSION_RETURNED_NULL =
            "Delegate factory expression returned NULL when resolving {0}.";

        public static readonly string UNABLE_TO_MATCH_IMPL_BASE_TYPES_WITH_SERVICE_TYPE =
            "Unable to match service with any of open-generic implementation {0} implemented types {1} when resolving {2}.";

        public static readonly string UNABLE_TO_FIND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE =
            "Unable to find for open-generic implementation {0} the type argument {1} when resolving {2}.";
    }

    public static class Registrator
    {
        /// <summary>
        /// Registers service <paramref name="serviceType"/>.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">Optional name of the service.</param>
        public static void Register(this IRegistrator registrator, Type serviceType, Factory factory, string named = null)
        {
            registrator.Register(factory, serviceType, named);
        }

        /// <summary>
        /// Registers service of <typeparamref name="TService"/>.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="factory"><see cref="Factory"/> details object.</param>
        /// <param name="named">Optional name of the service.</param>
        public static void Register<TService>(this IRegistrator registrator, Factory factory, string named = null)
        {
            registrator.Register(factory, typeof(TService), named);
        }

        /// <summary>
        /// Registers service <paramref name="serviceType"/> with corresponding <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The service type to register.</param>
        /// <param name="implementationType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="getConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        public static void Register(this IRegistrator registrator, Type serviceType,
            Type implementationType, IReuse reuse = null, GetConstructor getConstructor = null, FactorySetup setup = null,
            string named = null)
        {
            registrator.Register(new ReflectionFactory(implementationType, reuse, getConstructor, setup), serviceType, named);
        }

        /// <summary>
        /// Registers service of <paramref name="implementationType"/>. ServiceType will be the same as <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationType">Implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        public static void Register(this IRegistrator registrator,
            Type implementationType, IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            string named = null)
        {
            registrator.Register(new ReflectionFactory(implementationType, reuse, withConstructor, setup), implementationType, named);
        }

        /// <summary>
        /// Registers service of <typeparamref name="TService"/> type implemented by <typeparamref name="TImplementation"/> type.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        public static void Register<TService, TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            string named = null)
            where TImplementation : TService
        {
            registrator.Register(new ReflectionFactory(typeof(TImplementation), reuse, withConstructor, setup), typeof(TService), named);
        }

        /// <summary>
        /// Registers implementation type <typeparamref name="TImplementation"/> with itself as service type.
        /// </summary>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        public static void Register<TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            string named = null)
        {
            registrator.Register(new ReflectionFactory(typeof(TImplementation), reuse, withConstructor, setup), typeof(TImplementation), named);
        }

        public static Func<Type, bool> PublicTypes =
            type => (type.IsPublic || type.IsNestedPublic) && type != typeof(object);

        /// <summary>
        /// Registers single registration for all implemented public interfaces and base classes.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="implementationType">Service implementation type. Concrete and open-generic class are supported.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        /// <param name="types">Optional condition to include selected types only. Default value is <see cref="PublicTypes"/></param>
        public static void RegisterAll(this IRegistrator registrator,
            Type implementationType, IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            string named = null, Func<Type, bool> types = null)
        {
            var registration = new ReflectionFactory(implementationType, reuse, withConstructor, setup);

            var implementedTypes = implementationType.GetImplementedTypes(TypeTools.IncludeTypeItself.AsFirst);
            var implementedServiceTypes = implementedTypes.Where(types ?? PublicTypes);
            if (implementationType.IsGenericTypeDefinition)
            {
                var implTypeArgs = implementationType.GetGenericArguments();
                implementedServiceTypes = implementedServiceTypes.Where(t =>
                    t.IsGenericType && t.ContainsGenericParameters && t.ContainsAllGenericParameters(implTypeArgs))
                    .Select(t => t.GetGenericTypeDefinition());
            }

            var atLeastOneRegistered = false;
            foreach (var serviceType in implementedServiceTypes)
            {
                registrator.Register(registration, serviceType, named);
                atLeastOneRegistered = true;
            }

            Throw.If(!atLeastOneRegistered, "Unable to register any of implementation {0} implemented services {1}.",
                implementationType, implementedTypes);
        }

        /// <summary>
        /// Registers single registration for all implemented public interfaces and base classes.
        /// </summary>
        /// <typeparam name="TImplementation">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="withConstructor">Optional strategy to select constructor when multiple available.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional registration name.</param>
        public static void RegisterAll<TImplementation>(this IRegistrator registrator,
            IReuse reuse = null, GetConstructor withConstructor = null, FactorySetup setup = null,
            string named = null)
        {
            registrator.RegisterAll(typeof(TImplementation), reuse, withConstructor, setup, named);
        }

        /// <summary>
        /// Registers a factory delegate for creating an instance of <typeparamref name="TService"/>.
        /// Delegate can use <see cref="IResolver"/> parameter to resolve any required dependencies, e.g.:
        /// <code>RegisterDelegate&lt;ICar&gt;(r => new Car(r.Resolve&lt;IEngine&gt;()))</code>
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="lambda">The delegate used to create a instance of <typeparamref name="TService"/>.</param>
        /// <param name="reuse">Optional <see cref="IReuse"/> implementation, e.g. <see cref="Reuse.Singleton"/>. Default value means no reuse, aka Transient.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service name.</param>
        public static void RegisterDelegate<TService>(this IRegistrator registrator,
            Func<IResolver, TService> lambda, IReuse reuse = null, FactorySetup setup = null,
            string named = null)
        {
            var factory = new DelegateFactory(
                (request, registry) => Expression.Invoke(
                    request.Store.GetItemExpression(lambda), 
                    request.Store.GetItemExpression(registry)),
                reuse, setup);
            registrator.Register(factory, typeof(TService), named);
        }

        /// <summary>
        /// Registers a pre-created service instance of <typeparamref name="TService"/> 
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="instance">The pre-created instance of <typeparamref name="TService"/>.</param>
        /// <param name="setup">Optional factory setup, by default is (<see cref="ServiceSetup"/>)</param>
        /// <param name="named">Optional service name.</param>
        public static void RegisterInstance<TService>(this IRegistrator registrator,
            TService instance, FactorySetup setup = null,
            string named = null)
        {
            registrator.RegisterDelegate(_ => instance, Reuse.Transient, setup, named);
        }

        /// <summary>
        /// Returns true if <paramref name="serviceType"/> is registered in container or its open generic definition is registered in container.
        /// </summary>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceType">The type of the registered service.</param>
        /// <param name="named">Optional service name</param>
        /// <returns>True if <paramref name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered(this IRegistrator registrator, Type serviceType, string named = null)
        {
            return registrator.IsRegistered(serviceType, named);
        }

        /// <summary>
        /// Returns true if <typeparamref name="TService"/> type is registered in container or its open generic definition is registered in container.
        /// </summary>
        /// <typeparam name="TService">The type of service.</typeparam>
        /// <param name="registrator">Any <see cref="IRegistrator"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="named">Optional service name</param>
        /// <returns>True if <typeparamref name="TService"/> name="serviceType"/> is registered, false - otherwise.</returns>
        public static bool IsRegistered<TService>(this IRegistrator registrator, string named = null)
        {
            return registrator.IsRegistered(typeof(TService), named);
        }
    }

    public static class Resolver
    {
        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static object Resolve(this IResolver resolver, Type serviceType, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return resolver.ResolveDefault(serviceType, ifUnresolved);
        }

        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static TService Resolve<TService>(this IResolver resolver, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.ResolveDefault(typeof(TService), ifUnresolved);
        }

        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <param name="serviceType">The type of the requested service.</param>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceName">Service name.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static object Resolve(this IResolver resolver, Type serviceType, string serviceName, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return serviceName == null
                ? resolver.ResolveDefault(serviceType, ifUnresolved)
                : resolver.ResolveKeyed(serviceType, serviceName, ifUnresolved);
        }

        /// <summary>
        /// Returns an instance of statically known <typepsaramref name="TService"/> type.
        /// </summary>
        /// <typeparam name="TService">The type of the requested service.</typeparam>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="serviceName">Service name.</param>
        /// <param name="ifUnresolved">Optional, say to how to handle unresolved service case.</param>
        /// <returns>The requested service instance.</returns>
        public static TService Resolve<TService>(this IResolver resolver, string serviceName, IfUnresolved ifUnresolved = IfUnresolved.Throw)
        {
            return (TService)resolver.Resolve(typeof(TService), serviceName, ifUnresolved);
        }

        public static readonly BindingFlags MembersToResolve = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>
        /// For given instance resolves and sets non-initialized (null) properties from container.
        /// It does not throw if property is not resolved, so you might need to check property value afterwards.
        /// </summary>
        /// <param name="resolver">Any <see cref="IResolver"/> implementation, e.g. <see cref="Container"/>.</param>
        /// <param name="instance">Service instance with properties to resolve and initialize.</param>
        /// <param name="getServiceName">Optional function to find service name, otherwise service name will be null.</param>
        public static void ResolvePropertiesAndFields(this IResolver resolver, object instance, Func<MemberInfo, string> getServiceName = null)
        {
            var implType = instance.ThrowIfNull().GetType();
            getServiceName = getServiceName ?? (_ => null);

            foreach (var property in implType.GetProperties(MembersToResolve).Where(p => p.GetSetMethod() != null))
            {
                var value = resolver.Resolve(property.PropertyType, getServiceName(property), IfUnresolved.ReturnNull);
                if (value != null)
                    property.SetValue(instance, value, null);
            }

            foreach (var field in implType.GetFields(MembersToResolve).Where(f => !f.IsInitOnly))
            {
                var value = resolver.Resolve(field.FieldType, getServiceName(field), IfUnresolved.ReturnNull);
                if (value != null)
                    field.SetValue(instance, value);
            }
        }
    }

    public sealed class Request
    {
        public readonly Request Parent; // can be null for resolution root
        public readonly Type ServiceType;
        public readonly object ServiceKey; // null for default, string for named or integer index for multiple defaults.
        public readonly Type OpenGenericServiceType;
        public readonly DependencyInfo Dependency;

        public readonly int DecoratedFactoryID;

        public readonly int FactoryID;
        public readonly FactoryType FactoryType;
        public readonly Type ImplementationType;
        public readonly object Metadata;

        public IndexedStore Store;

        // Start from creating request, then Resolve it with factory and Push new sub-requests.
        public static Request Create(Type serviceType, object serviceKey = null, IndexedStore store = null)
        {
            return new Request(null, serviceType, serviceKey, store: store);
        }

        public Request Push(Type serviceType, object serviceKey, DependencyInfo dependency = null)
        {
            return new Request(this, serviceType, serviceKey, dependency, store: Store);
        }

        public Request PushPreservingParentKey(Type serviceType, DependencyInfo dependency = null)
        {
            return new Request(this, serviceType, ServiceKey, dependency, store: Store);
        }

        public Request ResolveTo(Factory factory)
        {
            for (var p = Parent; p != null; p = p.Parent)
                Throw.If(p.FactoryID == factory.ID && p.FactoryType == FactoryType.Service,
                    Error.RECURSIVE_DEPENDENCY_DETECTED, this);
            return new Request(Parent, ServiceType, ServiceKey, Dependency, DecoratedFactoryID, factory, Store);
        }

        public Request MakeDecorated()
        {
            return new Request(Parent, ServiceType, ServiceKey, Dependency, FactoryID, store: Store);
        }

        public Request GetNonWrapperParentOrDefault()
        {
            var p = Parent;
            while (p != null && p.FactoryType == FactoryType.GenericWrapper)
                p = p.Parent;
            return p;
        }

        public IEnumerable<Request> Enumerate()
        {
            for (var x = this; x != null; x = x.Parent)
                yield return x;
        }

        public override string ToString()
        {
            var message = new StringBuilder().Append(Print());
            return Parent == null ? message.ToString()
                 : Parent.Enumerate().Aggregate(message,
                    (m, r) => m.AppendLine().Append(" in ").Append(r.Print())).ToString();
        }

        #region Implementation

        private Request(Request parent, Type serviceType, object serviceKey, DependencyInfo dependency = null,
            int decoratedFactoryID = 0, Factory factory = null, IndexedStore store = null)
        {
            Parent = parent;
            ServiceKey = serviceKey;
            Dependency = dependency;

            ServiceType = serviceType.ThrowIfNull()
                .ThrowIf(serviceType.IsGenericTypeDefinition, Error.EXPECTED_CLOSED_GENERIC_SERVICE_TYPE, serviceType);

            OpenGenericServiceType = serviceType.IsGenericType ? serviceType.GetGenericTypeDefinition() : null;

            DecoratedFactoryID = decoratedFactoryID;
            if (factory != null)
            {
                FactoryType = factory.Setup.Type;
                FactoryID = factory.ID;
                ImplementationType = factory.ImplementationType;
                Metadata = factory.Setup.Metadata;
            }

            Store = store;
        }

        private string Print()
        {
            var key = ServiceKey is string ? "\"" + ServiceKey + "\""
                : ServiceKey is int ? "#" + ServiceKey
                : "unnamed";

            var kind = FactoryType == FactoryType.Decorator
                ? " decorator" : FactoryType == FactoryType.GenericWrapper
                ? " generic wrapper" : string.Empty;

            var type = ImplementationType != null && ImplementationType != ServiceType
                ? ImplementationType.Print() + " : " + ServiceType.Print()
                : ServiceType.Print();

            var dep = Dependency == null ? string.Empty : " (" + Dependency + ")";

            // example: "unnamed generic wrapper DryIoc.UnitTests.IService : DryIoc.UnitTests.Service (CtorParam service)"
            return key + kind + " " + type + dep;
        }

        #endregion
    }

    public enum FactoryType { Service, Decorator, GenericWrapper };

    public enum FactoryCachePolicy { ShouldNotCacheExpression, CouldCacheExpression };

    public abstract class FactorySetup
    {
        public abstract FactoryType Type { get; }
        public virtual FactoryCachePolicy CachePolicy { get { return FactoryCachePolicy.ShouldNotCacheExpression; } }
        public virtual object Metadata { get { return null; } }
    }

    public class ServiceSetup : FactorySetup
    {
        public static readonly ServiceSetup Default = new ServiceSetup();

        public static ServiceSetup With(FactoryCachePolicy cachePolicy = FactoryCachePolicy.CouldCacheExpression, object metadata = null)
        {
            return cachePolicy == FactoryCachePolicy.CouldCacheExpression && metadata == null ? Default : new ServiceSetup(cachePolicy, metadata);
        }

        public static ServiceSetup WithMetadata(object metadata = null)
        {
            return metadata == null ? Default : new ServiceSetup(metadata: metadata);
        }

        public override FactoryType Type { get { return FactoryType.Service; } }
        public override FactoryCachePolicy CachePolicy { get { return _cachePolicy; } }
        public override object Metadata { get { return _metadata; } }

        #region Implementation

        private ServiceSetup(FactoryCachePolicy cachePolicy = FactoryCachePolicy.CouldCacheExpression, object metadata = null)
        {
            _cachePolicy = cachePolicy;
            _metadata = metadata;
        }

        private readonly FactoryCachePolicy _cachePolicy;

        private readonly object _metadata;

        #endregion
    }

    public class GenericWrapperSetup : FactorySetup
    {
        public static readonly GenericWrapperSetup Default = new GenericWrapperSetup();

        public static GenericWrapperSetup With(Func<Type[], Type> selectServiceTypeArg)
        {
            return selectServiceTypeArg == null ? Default : new GenericWrapperSetup(selectServiceTypeArg);
        }

        public override FactoryType Type { get { return FactoryType.GenericWrapper; } }
        public readonly Func<Type[], Type> GetWrappedServiceType;

        #region Implementation

        private GenericWrapperSetup(Func<Type[], Type> selectServiceTypeFromGenericArgs = null)
        {
            GetWrappedServiceType = selectServiceTypeFromGenericArgs ?? SelectSingleByDefault;
        }

        private static Type SelectSingleByDefault(Type[] typeArgs)
        {
            Throw.If(typeArgs.Length != 1, Error.GENERIC_WRAPPER_EXPECTS_SINGLE_TYPE_ARG_BY_DEFAULT, typeArgs);
            return typeArgs[0];
        }

        #endregion
    }

    public class DecoratorSetup : FactorySetup
    {
        public static readonly DecoratorSetup Default = new DecoratorSetup();

        public static DecoratorSetup With(Func<Request, bool> condition = null)
        {
            return condition == null ? Default : new DecoratorSetup(condition);
        }

        public override FactoryType Type { get { return FactoryType.Decorator; } }
        public readonly Func<Request, bool> IsApplicable;

        #region Implementation

        private DecoratorSetup(Func<Request, bool> condition = null)
        {
            IsApplicable = condition ?? (_ => true);
        }

        #endregion
    }

    public abstract class Factory
    {
        public static readonly FactorySetup DefaultSetup = ServiceSetup.Default;

        public FactoryContext With(Request request, IRegistry registry)
        {
            return new FactoryContext(request, registry, this);
        }

        public readonly int ID;
        public readonly IReuse Reuse;

        public FactorySetup Setup
        {
            get { return _setup; }
            protected internal set { _setup = value ?? DefaultSetup; }
        }

        public virtual Type ImplementationType { get { return null; } }

        protected Factory(IReuse reuse = null, FactorySetup setup = null)
        {
            ID = Interlocked.Increment(ref _idSeedAndCount);
            Reuse = reuse;
            Setup = setup;
        }

        public virtual void ThrowIfCannotBeRegisteredWithServiceType(Type serviceType) { }

        public virtual bool ProvidesFactoryPerRequest { get { return false; } }

        //ncrunch: no coverage start
        public virtual Factory GetFactoryPerRequestOrDefault(Request request, IRegistry registry) { return null; }
        //ncrunch: no coverage end

        public abstract Expression CreateExpression(Request request, IRegistry registry);

        public virtual LambdaExpression CreateFuncWithArgsOrDefault(Type funcType, Request request, IRegistry registry, out IList<Type> unusedFuncArgs)
        {
            unusedFuncArgs = null;
            return null;
        }

        #region Implementation

        private static int _idSeedAndCount;
        private FactorySetup _setup;

        #endregion
    }

    public delegate ConstructorInfo GetConstructor(Type implementationType);

    public sealed class ReflectionFactory : Factory
    {
        public override Type ImplementationType { get { return _implementationType; } }

        public override bool ProvidesFactoryPerRequest { get { return _providesFactoryPerRequest; } }

        public ReflectionFactory(Type implementationType, IReuse reuse = null, GetConstructor getConstructor = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _implementationType = implementationType.ThrowIfNull()
                .ThrowIf(implementationType.IsAbstract, Error.EXPECTED_NON_ABSTRACT_IMPL_TYPE, implementationType);
            _providesFactoryPerRequest = _implementationType.IsGenericTypeDefinition;
            _getConstructor = getConstructor;
        }

        public override void ThrowIfCannotBeRegisteredWithServiceType(Type serviceType)
        {
            var implType = _implementationType;
            if (!implType.IsGenericTypeDefinition)
            {
                if (implType.IsGenericType && implType.ContainsGenericParameters)
                    Throw.It(Error.USUPPORTED_REGISTRATION_OF_NON_GENERIC_IMPL_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS,
                        implType, implType.GetGenericTypeDefinition());

                if (implType != serviceType && serviceType != typeof(object))
                    Throw.If(Array.IndexOf(implType.GetImplementedTypes(), serviceType) == -1,
                        Error.EXPECTED_IMPL_TYPE_ASSIGNABLE_TO_SERVICE_TYPE, implType, serviceType);
            }
            else if (implType != serviceType)
            {
                if (serviceType.IsGenericTypeDefinition)
                {
                    var implementedTypes = implType.GetImplementedTypes();
                    var implementedOpenGenericTypes = implementedTypes.Where(t =>
                        t.IsGenericType && t.ContainsGenericParameters && t.GetGenericTypeDefinition() == serviceType);

                    var implTypeArgs = implType.GetGenericArguments();
                    Throw.If(!implementedOpenGenericTypes.Any(t => t.ContainsAllGenericParameters(implTypeArgs)),
                        Error.UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_CAUSE_SERVICE_DOES_NOT_SPECIFY_ALL_TYPE_ARGS,
                        implType, serviceType, implementedOpenGenericTypes);
                }
                else if (implType.IsGenericType && serviceType.ContainsGenericParameters)
                    Throw.It(Error.USUPPORTED_REGISTRATION_OF_NON_GENERIC_SERVICE_TYPE_DEFINITION_BUT_WITH_GENERIC_ARGS,
                        serviceType, serviceType.GetGenericTypeDefinition());
                else
                    Throw.It(Error.UNABLE_TO_REGISTER_OPEN_GENERIC_IMPL_WITH_NON_GENERIC_SERVICE, implType, serviceType);
            }
        }

        public override Factory GetFactoryPerRequestOrDefault(Request request, IRegistry _)
        {
            var closedTypeArgs = _implementationType == request.OpenGenericServiceType
                ? request.ServiceType.GetGenericArguments()
                : GetClosedTypeArgsForGenericImplementationType(_implementationType, request);

            var closedImplType = _implementationType.MakeGenericType(closedTypeArgs);

            return new ReflectionFactory(closedImplType, Reuse, _getConstructor, Setup);
        }

        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            var ctor = GetConstructor(_implementationType);
            var ctorParams = ctor.GetParameters();
            Expression[] paramExprs = null;
            if (ctorParams.Length != 0)
            {
                paramExprs = new Expression[ctorParams.Length];
                for (var i = 0; i < ctorParams.Length; i++)
                {
                    var ctorParam = ctorParams[i];
                    var paramKey = request.FactoryType != FactoryType.Service ? request.ServiceKey // propagate key from wrapper or decorator.
                        : registry.ResolutionRules.ForConstructorParameter.Invoke(r => r(ctorParam, request, registry));
                    var paramRequest = request.Push(ctorParam.ParameterType, paramKey,
                        new DependencyInfo(DependencyKind.CtorParam, ctorParam.Name));
                    paramExprs[i] = registry.GetOrAddFactory(paramRequest, IfUnresolved.Throw).GetExpression();
                }
            }

            var newExpr = Expression.New(ctor, paramExprs);
            return InitMembersIfRequired(_implementationType, newExpr, request, registry);
        }

        public override LambdaExpression CreateFuncWithArgsOrDefault(Type funcType, Request request, IRegistry registry, out IList<Type> unusedFuncArgs)
        {
            var funcParamTypes = funcType.GetGenericArguments();
            funcParamTypes.ThrowIf(funcParamTypes.Length == 1, Error.EXPECTED_FUNC_WITH_MULTIPLE_ARGS, funcType);

            var ctor = GetConstructor(_implementationType);
            var ctorParams = ctor.GetParameters();
            var ctorParamExprs = new Expression[ctorParams.Length];
            var funcInputParamExprs = new ParameterExpression[funcParamTypes.Length - 1]; // (minus Func return parameter).

            for (var cp = 0; cp < ctorParams.Length; cp++)
            {
                var ctorParam = ctorParams[cp];
                for (var fp = 0; fp < funcParamTypes.Length - 1; fp++)
                {
                    var funcParamType = funcParamTypes[fp];
                    if (ctorParam.ParameterType == funcParamType &&
                        funcInputParamExprs[fp] == null) // Skip if Func parameter was already used for constructor.
                    {
                        ctorParamExprs[cp] = funcInputParamExprs[fp] = Expression.Parameter(funcParamType, ctorParam.Name);
                        break;
                    }
                }

                if (ctorParamExprs[cp] == null) // If no matching constructor parameter found in Func, resolve it from Container.
                {
                    var paramKey = request.FactoryType != FactoryType.Service ? request.ServiceKey // propagate key from wrapper or decorator.
                        : registry.ResolutionRules.ForConstructorParameter.Invoke(r => r(ctorParam, request, registry));
                    var paramRequest = request.Push(ctorParam.ParameterType, paramKey,
                        new DependencyInfo(DependencyKind.CtorParam, ctorParam.Name));
                    ctorParamExprs[cp] = registry.GetOrAddFactory(paramRequest, IfUnresolved.Throw).GetExpression();
                }
            }

            // Find unused Func parameters (present in Func but not in constructor) and create "_" (ignored) Parameter expressions for them.
            // In addition store unused parameter in output list for client review.
            unusedFuncArgs = null;
            for (var fp = 0; fp < funcInputParamExprs.Length; fp++)
            {
                if (funcInputParamExprs[fp] == null) // unused parameter
                {
                    if (unusedFuncArgs == null) unusedFuncArgs = new List<Type>(2);
                    var funcParamType = funcParamTypes[fp];
                    unusedFuncArgs.Add(funcParamType);
                    funcInputParamExprs[fp] = Expression.Parameter(funcParamType, "_");
                }
            }

            var newExpr = Expression.New(ctor, ctorParamExprs);
            var newExprInitialized = InitMembersIfRequired(_implementationType, newExpr, request, registry);
            return Expression.Lambda(funcType, newExprInitialized, funcInputParamExprs);
        }

        #region Implementation

        private readonly Type _implementationType;
        private readonly bool _providesFactoryPerRequest;
        private readonly GetConstructor _getConstructor;

        public ConstructorInfo GetConstructor(Type type)
        {
            if (_getConstructor != null)
                return _getConstructor(type);

            var constructors = type.GetConstructors();
            Throw.If(constructors.Length == 0, Error.NO_PUBLIC_CONSTRUCTOR_DEFINED, type);
            Throw.If(constructors.Length > 1, Error.UNABLE_TO_SELECT_CONSTRUCTOR, constructors.Length, type);
            return constructors[0];
        }

        private static Expression InitMembersIfRequired(Type implementationType, NewExpression newService, Request request, IRegistry registry)
        {
            if (registry.ResolutionRules.ForPropertyOrField.IsEmpty)
                return newService;

            var properties = implementationType.GetProperties(Resolver.MembersToResolve).Where(p => p.GetSetMethod() != null);
            var fields = implementationType.GetFields(Resolver.MembersToResolve).Where(f => !f.IsInitOnly);

            var bindings = new List<MemberBinding>();
            foreach (var member in properties.Cast<MemberInfo>().Concat(fields.Cast<MemberInfo>()))
            {
                var pf = member;
                object key = null;
                if (registry.ResolutionRules.ForPropertyOrField.Invoke(r => r(out key, pf, request, registry)))
                {
                    var memberRequest = request.Push(pf.GetMemberType(), key,
                        new DependencyInfo(pf is PropertyInfo ? DependencyKind.Property : DependencyKind.Field, pf.Name));
                    var memberExpr = registry.GetOrAddFactory(memberRequest, IfUnresolved.Throw).GetExpression();
                    bindings.Add(Expression.Bind(pf, memberExpr));
                }
            }

            return bindings.Count == 0 ? (Expression)newService : Expression.MemberInit(newService, bindings);
        }

        private static Type[] GetClosedTypeArgsForGenericImplementationType(Type implType, Request request)
        {
            var serviceTypeArgs = request.ServiceType.GetGenericArguments();
            var serviceTypeGenericDefinition = request.OpenGenericServiceType;

            var openImplTypeArgs = implType.GetGenericArguments();
            var implementedTypes = implType.GetImplementedTypes();

            Type[] resultImplTypeArgs = null;
            for (var i = 0; resultImplTypeArgs == null && i < implementedTypes.Length; i++)
            {
                var implementedType = implementedTypes[i];
                if (implementedType.IsGenericType && implementedType.ContainsGenericParameters &&
                    implementedType.GetGenericTypeDefinition() == serviceTypeGenericDefinition)
                {
                    var matchedTypeArgs = new Type[openImplTypeArgs.Length];
                    if (MatchServiceWithImplementedTypeArgs(ref matchedTypeArgs,
                        openImplTypeArgs, implementedType.GetGenericArguments(), serviceTypeArgs))
                        resultImplTypeArgs = matchedTypeArgs;
                }
            }

            resultImplTypeArgs = resultImplTypeArgs.ThrowIfNull(
                Error.UNABLE_TO_MATCH_IMPL_BASE_TYPES_WITH_SERVICE_TYPE, implType, implementedTypes, request);

            var unmatchedArgIndex = Array.IndexOf(resultImplTypeArgs, null);
            if (unmatchedArgIndex != -1)
                Throw.It(Error.UNABLE_TO_FIND_OPEN_GENERIC_IMPL_TYPE_ARG_IN_SERVICE,
                    implType, openImplTypeArgs[unmatchedArgIndex], request);

            return resultImplTypeArgs;
        }

        private static bool MatchServiceWithImplementedTypeArgs(ref Type[] matchedImplArgs,
            Type[] openImplementationArgs, Type[] openImplementedArgs, Type[] closedServiceArgs)
        {
            for (var i = 0; i < openImplementedArgs.Length; i++)
            {
                var openImplementedArg = openImplementedArgs[i];
                var closedServiceArg = closedServiceArgs[i];
                if (openImplementedArg.IsGenericParameter)
                {
                    var matchedIndex = Array.FindIndex(openImplementationArgs, t => t.Name == openImplementedArg.Name);
                    if (matchedIndex != -1)
                    {
                        if (matchedImplArgs[matchedIndex] == null)
                            matchedImplArgs[matchedIndex] = closedServiceArg;
                        else if (matchedImplArgs[matchedIndex] != closedServiceArg)
                            return false; // more than one closedServiceArg is matching with single openArg
                    }
                }
                else if (openImplementedArg != closedServiceArg)
                {
                    if (!openImplementedArg.IsGenericType || !openImplementedArg.ContainsGenericParameters ||
                        !closedServiceArg.IsGenericType ||
                        closedServiceArg.GetGenericTypeDefinition() != openImplementedArg.GetGenericTypeDefinition())
                        return false; // openArg and closedArg are different types

                    if (!MatchServiceWithImplementedTypeArgs(ref matchedImplArgs, openImplementationArgs,
                        openImplementedArg.GetGenericArguments(), closedServiceArg.GetGenericArguments()))
                        return false; // nested match failed due either one of above reasons.
                }
            }

            return true;
        }

        #endregion
    }

    public sealed class DelegateFactory : Factory
    {
        public DelegateFactory(Func<Request, IRegistry, Expression> getExpression, IReuse reuse = null, FactorySetup setup = null)
            : base(reuse, setup)
        {
            _getExpression = getExpression.ThrowIfNull();
        }

        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            return _getExpression(request, registry).ThrowIfNull(Error.DELEGATE_FACTORY_EXPRESSION_RETURNED_NULL, request);
        }

        #region Implementation

        private readonly Func<Request, IRegistry, Expression> _getExpression;

        #endregion
    }

    public sealed class FactoryProvider : Factory
    {
        public override bool ProvidesFactoryPerRequest { get { return true; } }

        public FactoryProvider(Func<Request, IRegistry, Factory> getFactoryOrDefault, FactorySetup setup = null)
            : base(setup: setup)
        {
            _getFactoryOrDefault = getFactoryOrDefault.ThrowIfNull();
        }

        public override Factory GetFactoryPerRequestOrDefault(Request request, IRegistry registry)
        {
            var factory = _getFactoryOrDefault(request, registry);
            if (factory != null && factory.Setup == DefaultSetup)
                factory.Setup = Setup; // propagate provider setup if it is not specified by client.
            return factory;
        }

        //ncrunch: no coverage start
        public override Expression CreateExpression(Request request, IRegistry registry)
        {
            throw new NotSupportedException();
        }
        //ncrunch: no coverage end

        private readonly Func<Request, IRegistry, Factory> _getFactoryOrDefault;
    }

    public enum DependencyKind { CtorParam, Property, Field }

    public sealed class DependencyInfo
    {
        public readonly DependencyKind Kind;
        public readonly string Name;

        public DependencyInfo(DependencyKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public override string ToString()
        {
            return Kind + " " + Name;
        }
    }

    public sealed class Scope : IDisposable
    {
        public T GetOrAdd<T>(int id, Func<T> factory)
        {
            Throw.If(_disposed == 1, Error.SCOPE_IS_DISPOSED);
            lock (_syncRoot)
            {
                var item = _items.GetValueOrDefault(id);
                if (item == null)
                    _items = _items.AddOrUpdate(id, item = factory());
                return (T)item;
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            foreach (var item in _items.TraverseInOrder().Select(x => x.Value).OfType<IDisposable>())
                item.Dispose();
            _items = null;
        }

        #region Implementation

        private readonly object _syncRoot = new object();
        private HashTree<int, object> _items = HashTree<int, object>.Empty;
        private int _disposed;

        #endregion
    }

    public interface IReuse
    {
        Expression Apply(Request request, IRegistry registry, int factoryID, Expression factoryExpr);
    }

    public static class Reuse
    {
        public static readonly IReuse Transient = null; // no reuse.
        public static readonly IReuse Singleton = new SingletonReuse();
        public static readonly IReuse InCurrentScope = new CurrentScopeReuse();
        public static readonly IReuse InResolutionScope = new ResolutionScopeReuse(Container.ResolutionScopeParameter);

        public static Expression GetScopedServiceExpression(Expression scope, int factoryID, Expression factoryExpr)
        {
            return Expression.Call(scope,
                _getOrAddToScopeMethod.MakeGenericMethod(factoryExpr.Type),
                Expression.Constant(factoryID),
                Expression.Lambda(factoryExpr, null));
        }

        #region Implementation

        private static readonly MethodInfo _getOrAddToScopeMethod = typeof (Scope).GetMethod("GetOrAdd");

        private sealed class SingletonReuse : IReuse
        {
            public Expression Apply(Request request, IRegistry registry, int factoryID, Expression factoryExpr)
            {
                // Create lazy singleton if we have Func somewhere in dependency chain.
                var parent = request.Parent;
                if (parent != null && parent.Enumerate().Any(p =>
                    p.OpenGenericServiceType != null && ContainerSetup.FuncTypes.Contains(p.OpenGenericServiceType)))
                {
                    var singletonScopeExpr = request.Store.GetItemExpression(registry.SingletonScope);
                    return GetScopedServiceExpression(singletonScopeExpr, factoryID, factoryExpr);
                }

                // Create singleton object now and put it into store.
                var singleton = registry.SingletonScope.GetOrAdd(factoryID,
                    () => factoryExpr.CompileToFactory().Invoke(request.Store, null));

                var singletonExpr = request.Store.GetItemExpression(singleton, factoryExpr.Type);
                return singletonExpr;
            }
        }

        private sealed class CurrentScopeReuse : IReuse
        {
            public Expression Apply(Request request, IRegistry registry, int factoryID, Expression factoryExpr)
            {
                var currentScopeExpr = request.Store.GetItemExpression(registry.CurrentScope);
                return GetScopedServiceExpression(currentScopeExpr, factoryID, factoryExpr);
            }
        }

        private sealed class ResolutionScopeReuse : IReuse
        {
            public ResolutionScopeReuse(ParameterExpression scopeParameterExpr)
            {
                _scopeExpr = Expression.Call(typeof(ResolutionScopeReuse), "GetScope", null, scopeParameterExpr);
            }

            public Expression Apply(Request _, IRegistry __, int factoryID, Expression factoryExpr)
            {
                return GetScopedServiceExpression(_scopeExpr, factoryID, factoryExpr);
            }

            // ReSharper disable UnusedMember.Local
            public static Scope GetScope(ref Scope scope)
            {
                return scope = scope ?? new Scope();
            }
            // ReSharper restore UnusedMember.Local

            private readonly Expression _scopeExpr;
        }

        #endregion
    }

    public enum IfUnresolved { Throw, ReturnNull }

    public interface IResolver
    {
        object ResolveDefault(Type serviceType, IfUnresolved ifUnresolved);

        object ResolveKeyed(Type serviceType, object serviceKey, IfUnresolved ifUnresolved);
    }

    public interface IRegistrator
    {
        Factory Register(Factory factory, Type serviceType, object serviceKey);

        bool IsRegistered(Type serviceType, string serviceName);
    }

    public interface IRegistry : IResolver, IRegistrator
    {
        WeakReference SelfWeakRef { get; }
        ResolutionRules ResolutionRules { get; }
        Scope CurrentScope { get; }
        Scope SingletonScope { get; }

        FactoryContext GetOrAddFactory(Request request, IfUnresolved ifUnresolved);

        Factory GetFactoryOrDefault(Type serviceType, object serviceKey);

        Expression GetDecoratorExpressionOrDefault(Request request, IRegistry registry);

        IEnumerable<object> GetKeys(Type serviceType, Func<Factory, bool> condition);

        Type GetWrappedServiceTypeOrSelf(Type serviceType);

        Expression GetCachedFactoryExpression(int factoryID);
        void CacheFactoryExpression(int factoryID, Expression result);
    }

    public class FactoryContext
    {
        public readonly Factory Factory;
        public readonly Request Request;
        public readonly IRegistry Registry;

        public FactoryContext(Request request, IRegistry registry, Factory factory)
        {
            Factory = factory.ThrowIfNull();
            Request = request.ThrowIfNull().ResolveTo(factory);
            Registry = registry.ThrowIfNull();
        }

        public Expression GetExpression()
        {
            var decorator = Registry.GetDecoratorExpressionOrDefault(Request, Registry);
            if (decorator != null && !(decorator is LambdaExpression))
                return decorator;

            var expression = Registry.GetCachedFactoryExpression(Factory.ID);
            if (expression == null)
            {
                expression = Factory.CreateExpression(Request, Registry);
                if (Factory.Reuse != null)
                    expression = Factory.Reuse.Apply(Request, Registry, Factory.ID, expression);
                if (Factory.Setup.CachePolicy == FactoryCachePolicy.CouldCacheExpression)
                    Registry.CacheFactoryExpression(Factory.ID, expression);
            }

            if (decorator != null)
                expression = Expression.Invoke(decorator, expression);

            return expression;
        }

        public LambdaExpression GetFuncWithArgsOrDefault(Type funcType, out IList<Type> unusedFuncArgs)
        {
            var func = Factory.CreateFuncWithArgsOrDefault(funcType, Request, Registry, out unusedFuncArgs);
            if (func == null)
                return null;

            var decorator = Registry.GetDecoratorExpressionOrDefault(Request, Registry);
            if (decorator != null && !(decorator is LambdaExpression))
                return Expression.Lambda(funcType, decorator, func.Parameters);

            if (Factory.Reuse != null)
                func = Expression.Lambda(funcType, Factory.Reuse.Apply(Request, Registry, Factory.ID, func.Body), func.Parameters);

            if (decorator != null)
                func = Expression.Lambda(funcType, Expression.Invoke(decorator, func.Body), func.Parameters);

            return func;
        }
    }

    public sealed class Many<TService>
    {
        public readonly IEnumerable<TService> Items;

        public Many(IEnumerable<TService> items)
        {
            Items = items;
        }
    }

    public sealed class Meta<TService, TMetadata>
    {
        public readonly TService Value;

        public readonly TMetadata Metadata;

        public Meta(TService service, TMetadata metadata)
        {
            Value = service;
            Metadata = metadata;
        }
    }

    [DebuggerDisplay("{Expression}")]
    public sealed class DebugExpression<TService>
    {
        public readonly Expression<CompiledFactory> Expression;

        public DebugExpression(Expression<CompiledFactory> expression)
        {
            Expression = expression;
        }
    }

    public class ContainerException : InvalidOperationException
    {
        public ContainerException(string message) : base(message) { }
    }

    public static class Throw
    {
        public static Func<string, Exception> GetException = message => new ContainerException(message);

        public static Func<object, string> PrintArg = Sugar.Print;

        public static T ThrowIfNull<T>(this T arg, string message = null, object arg0 = null, object arg1 = null, object arg2 = null) where T : class
        {
            if (arg != null) return arg;
            throw GetException(message == null ? Format(ARG_IS_NULL, typeof(T)) : Format(message, arg0, arg1, arg2));
        }

        public static T ThrowIf<T>(this T arg, bool throwCondition, string message = null, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            if (!throwCondition) return arg;
            throw GetException(message == null ? Format(ARG_HAS_IMVALID_CONDITION, typeof(T)) : Format(message, arg0, arg1, arg2));
        }

        public static void If(bool throwCondition, string message, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            if (!throwCondition) return;
            throw GetException(Format(message, arg0, arg1, arg2));
        }

        public static void It(string message, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            throw GetException(Format(message, arg0, arg1, arg2));
        }

        private static string Format(this string message, object arg0 = null, object arg1 = null, object arg2 = null)
        {
            return string.Format(message, PrintArg(arg0), PrintArg(arg1), PrintArg(arg2));
        }

        private static readonly string ARG_IS_NULL = "Argument of type {0} is null.";
        private static readonly string ARG_HAS_IMVALID_CONDITION = "Argument of type {0} has invalid condition.";
    }

    public static class TypeTools
    {
        // ReSharper disable ConstantNullCoalescingCondition
        public static Func<Type, string> PrintDetailsDefault = t => t.FullName ?? t.Name;
        // ReSharper restore ConstantNullCoalescingCondition

        public static string Print(this Type type, Func<Type, string> printDetails = null)
        {
            if (type == null) return null;
            printDetails = printDetails ?? PrintDetailsDefault;
            var name = printDetails(type);
            if (type.IsGenericType) // for generic types
            {
                var genericArgs = type.GetGenericArguments();
                var genericArgsString = type.IsGenericTypeDefinition
                    ? new string(',', genericArgs.Length - 1)
                    : String.Join(", ", genericArgs.Select(x => x.Print(printDetails)).ToArray());
                name = name.Substring(0, name.IndexOf('`')) + "<" + genericArgsString + ">";
            }
            return name.Replace('+', '.'); // for nested classes
        }

        public enum IncludeTypeItself { No, AsFirst }

        /// <summary>
        /// Returns all type interfaces and base types except object.
        /// </summary>
        public static Type[] GetImplementedTypes(this Type type, IncludeTypeItself includeTypeItself = IncludeTypeItself.No)
        {
            Type[] results;

            var interfaces = type.GetInterfaces();
            var interfaceStartIndex = includeTypeItself == IncludeTypeItself.AsFirst ? 1 : 0;
            var selfPlusInterfaceCount = interfaceStartIndex + interfaces.Length;

            var baseType = type.BaseType;
            if (baseType == null || baseType == typeof(object))
                results = new Type[selfPlusInterfaceCount];
            else
            {
                List<Type> baseBaseTypes = null;
                for (var bb = baseType.BaseType; bb != null && bb != typeof(object); bb = bb.BaseType)
                    (baseBaseTypes ?? (baseBaseTypes = new List<Type>(2))).Add(bb);

                if (baseBaseTypes == null)
                    results = new Type[selfPlusInterfaceCount + 1];
                else
                {
                    results = new Type[selfPlusInterfaceCount + 1 + baseBaseTypes.Count];
                    baseBaseTypes.CopyTo(results, selfPlusInterfaceCount + 1);
                }

                results[selfPlusInterfaceCount] = baseType;
            }

            if (includeTypeItself == IncludeTypeItself.AsFirst)
                results[0] = type;

            if (interfaces.Length == 1)
                results[interfaceStartIndex] = interfaces[0];
            else if (interfaces.Length > 1)
                Array.Copy(interfaces, 0, results, interfaceStartIndex, interfaces.Length);

            return results;
        }

        public static bool ContainsAllGenericParameters(this Type similarType, Type[] genericParameters)
        {
            var argNames = new string[genericParameters.Length];
            for (var i = 0; i < genericParameters.Length; i++)
                argNames[i] = genericParameters[i].Name;

            MarkTargetGenericParameters(similarType.GetGenericArguments(), ref argNames);

            for (var i = 0; i < argNames.Length; i++)
                if (argNames[i] != null)
                    return false;

            return true;
        }

        #region Implementation

        private static void MarkTargetGenericParameters(Type[] sourceTypeArgs, ref string[] targetArgNames)
        {
            for (var i = 0; i < sourceTypeArgs.Length; i++)
            {
                var sourceTypeArg = sourceTypeArgs[i];
                if (sourceTypeArg.IsGenericParameter)
                {
                    var matchingTargetArgIndex = Array.IndexOf(targetArgNames, sourceTypeArg.Name);
                    if (matchingTargetArgIndex != -1)
                        targetArgNames[matchingTargetArgIndex] = null;
                }
                else if (sourceTypeArg.IsGenericType && sourceTypeArg.ContainsGenericParameters)
                    MarkTargetGenericParameters(sourceTypeArg.GetGenericArguments(), ref targetArgNames);
            }
        }

        #endregion
    }

    public static class Sugar
    {
        public static string Print(object x)
        {
            return x is string ? (string)x
                : (x is Type ? ((Type)x).Print()
                : (x is IEnumerable<Type> ? ((IEnumerable)x).Print(";" + Environment.NewLine, ifEmpty: "<empty>")
                : (x is IEnumerable ? ((IEnumerable)x).Print(ifEmpty: "<empty>")
                : (string.Empty + x))));
        }

        public static string Print(this IEnumerable items,
            string separator = ", ", Func<object, string> printItem = null, string ifEmpty = null)
        {
            if (items == null) return null;
            printItem = printItem ?? Print;
            var builder = new StringBuilder();
            foreach (var item in items)
                (builder.Length == 0 ? builder : builder.Append(separator)).Append(printItem(item));
            var result = builder.ToString();
            return result != string.Empty ? result : (ifEmpty ?? string.Empty);
        }

        public static Type GetMemberType(this MemberInfo member)
        {
            var mt = member.MemberType;
            mt.ThrowIf(mt != MemberTypes.Field && mt != MemberTypes.Property);
            return mt == MemberTypes.Field ? ((FieldInfo)member).FieldType : ((PropertyInfo)member).PropertyType;
        }

        public static V GetOrAdd<K, V>(this IDictionary<K, V> source, K key, Func<K, V> valueFactory)
        {
            V value;
            if (!source.TryGetValue(key, out value))
                source.Add(key, value = valueFactory(key));
            return value;
        }

        public static T[] Append<T>(this T[] source, params T[] added)
        {
            if (added == null || added.Length == 0)
                return source;
            if (source == null || source.Length == 0)
                return added;
            var result = new T[source.Length + added.Length];
            Array.Copy(source, 0, result, 0, source.Length);
            if (added.Length == 1)
                result[source.Length] = added[0];
            else
                Array.Copy(added, 0, result, source.Length, added.Length);
            return result;
        }

        public static T[] AppendOrUpdate<T>(this T[] source, T value, int index = -1)
        {
            var sourceLength = source.Length;
            index = index < 0 ? sourceLength : index;
            var result = new T[index < sourceLength ? sourceLength : sourceLength + 1];
            Array.Copy(source, result, sourceLength);
            result[index] = value;
            return result;
        }
    }

    public class KV<K, V>
    {
        public readonly K Key;
        public readonly V Value;

        public KV(K key, V value)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>
    /// Immutable kind of http://en.wikipedia.org/wiki/AVL_tree, where actual node key is <typeparamref name="K"/> hash code.
    /// </summary>
    public sealed class HashTree<K, V>
    {
        public static readonly HashTree<K, V> Empty = new HashTree<K, V>();

        public readonly int Hash;
        public readonly K Key;
        public readonly V Value;
        public readonly KV<K, V>[] Conflicts;
        public readonly HashTree<K, V> Left, Right;
        public readonly int Height;

        public bool IsEmpty { get { return Height == 0; } }

        public delegate V UpdateValue(V current, V added);

        public HashTree<K, V> AddOrUpdate(K key, V value, UpdateValue updateValue = null)
        {
            return AddOrUpdate(key.GetHashCode(), key, value, updateValue ?? ReplaceValue);
        }

        public V GetValueOrDefault(K key, V defaultValue = default(V))
        {
            var t = this;
            var hash = key.GetHashCode();
            while (t.Height != 0 && t.Hash != hash)
                t = hash < t.Hash ? t.Left : t.Right;
            return t.Height != 0 && (ReferenceEquals(key, t.Key) || key.Equals(t.Key)) ? t.Value
                : t.GetConflictedValueOrDefault(key, defaultValue);
        }

        /// <summary>
        /// Depth-first in-order traversal as described in http://en.wikipedia.org/wiki/Tree_traversal
        /// The only difference is using fixed size array instead of stack for speed-up (~20% faster than stack).
        /// </summary>
        public IEnumerable<KV<K, V>> TraverseInOrder()
        {
            var parents = new HashTree<K, V>[Height];
            var parentCount = -1;
            var t = this;
            while (!t.IsEmpty || parentCount != -1)
            {
                if (!t.IsEmpty)
                {
                    parents[++parentCount] = t;
                    t = t.Left;
                }
                else
                {
                    t = parents[parentCount--];
                    yield return new KV<K, V>(t.Key, t.Value);
                    if (t.Conflicts != null)
                        for (var i = 0; i < t.Conflicts.Length; i++)
                            yield return t.Conflicts[i];
                    t = t.Right;
                }
            }
        }

        #region Implementation

        private HashTree() { }

        private HashTree(int hash, K key, V value, KV<K, V>[] conficts, HashTree<K, V> left, HashTree<K, V> right)
        {
            Hash = hash;
            Key = key;
            Value = value;
            Conflicts = conficts;
            Left = left;
            Right = right;
            Height = 1 + (left.Height > right.Height ? left.Height : right.Height);
        }

        private static V ReplaceValue(V _, V added) { return added; }

        private HashTree<K, V> AddOrUpdate(int hash, K key, V value, UpdateValue updateValue)
        {
            return Height == 0 ? new HashTree<K, V>(hash, key, value, null, Empty, Empty)
                : (hash == Hash ? ResolveConflicts(key, value, updateValue)
                : (hash < Hash
                    ? With(Left.AddOrUpdate(hash, key, value, updateValue), Right)
                    : With(Left, Right.AddOrUpdate(hash, key, value, updateValue)))
                        .EnsureBalanced());
        }

        private HashTree<K, V> ResolveConflicts(K key, V value, UpdateValue updateValue)
        {
            if (ReferenceEquals(Key, key) || Key.Equals(key))
                return new HashTree<K, V>(Hash, key, updateValue(Value, value), Conflicts, Left, Right);

            if (Conflicts == null)
                return new HashTree<K, V>(Hash, Key, Value, new[] { new KV<K, V>(key, value) }, Left, Right);

            var i = Conflicts.Length - 1;
            while (i >= 0 && !Equals(Conflicts[i].Key, Key)) i--;
            var conflicts = new KV<K, V>[i != -1 ? Conflicts.Length : Conflicts.Length + 1];
            Array.Copy(Conflicts, 0, conflicts, 0, Conflicts.Length);
            conflicts[i != -1 ? i : Conflicts.Length] = new KV<K, V>(key, i != -1 ? updateValue(Conflicts[i].Value, value) : value);
            return new HashTree<K, V>(Hash, Key, Value, conflicts, Left, Right);
        }

        private V GetConflictedValueOrDefault(K key, V defaultValue)
        {
            if (Conflicts != null)
                for (var i = 0; i < Conflicts.Length; i++)
                    if (Equals(Conflicts[i].Key, key))
                        return Conflicts[i].Value;
            return defaultValue;
        }

        private HashTree<K, V> EnsureBalanced()
        {
            var delta = Left.Height - Right.Height;
            return delta >= 2 ? With(Left.Right.Height - Left.Left.Height == 1 ? Left.RotateLeft() : Left, Right).RotateRight()
                : (delta <= -2 ? With(Left, Right.Left.Height - Right.Right.Height == 1 ? Right.RotateRight() : Right).RotateLeft()
                : this);
        }

        private HashTree<K, V> RotateRight()
        {
            return Left.With(Left.Left, With(Left.Right, Right));
        }

        private HashTree<K, V> RotateLeft()
        {
            return Right.With(With(Left, Right.Left), Right.Right);
        }

        private HashTree<K, V> With(HashTree<K, V> left, HashTree<K, V> right)
        {
            return new HashTree<K, V>(Hash, Key, Value, Conflicts, left, right);
        }

        #endregion
    }
}