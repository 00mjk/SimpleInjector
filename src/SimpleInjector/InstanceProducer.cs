﻿// Copyright (c) Simple Injector Contributors. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

namespace SimpleInjector
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Linq.Expressions;
    using SimpleInjector.Advanced;
    using SimpleInjector.Diagnostics;
    using SimpleInjector.Diagnostics.Analyzers;
    using SimpleInjector.Internals;
    using SimpleInjector.Lifestyles;

    /// <summary>
    /// Produces instances for a given registration. Instances of this type are generally created by the
    /// container when calling one of the <b>Register</b> overloads. Instances can be retrieved by calling
    /// <see cref="Container.GetCurrentRegistrations()">GetCurrentRegistrations()</see> or
    /// <see cref="Container.GetRegistration(Type, bool)">GetRegistration(Type, bool)</see>.
    /// </summary>
    /// <remarks>
    /// The <b>Register</b> method overloads create <b>InstanceProducer</b> instances internally, but
    /// <b>InstanceProducer</b>s can be created manually to implement special scenarios. An
    /// <b>InstanceProducer</b> wraps <see cref="Registration"/> instance. The <b>Registration</b> builds an
    /// <see cref="Expression"/> that describes the intend to create the instance according to a certain
    /// lifestyle. The <b>InstanceProducer</b> on the other hand transforms this <b>Expression</b> to a
    /// delegate and allows the actual instance to be created. A <b>Registration</b> itself can't create any
    /// instance. The <b>InsanceProducer</b> allows intercepting created instances by hooking onto the
    /// <see cref="Container.ExpressionBuilt">Container.ExpressionBuilt</see> event. The
    /// <see cref="Container.RegisterDecorator(Type, Type)">RegisterDecorator</see> methods for
    /// instance work by hooking onto the <b>ExpressionBuilt</b> event and allow wrapping the returned instance
    /// with a decorator.
    /// </remarks>
    /// <example>
    /// The following example shows the creation of two different <b>InstanceProducer</b> instances that wrap
    /// the same <b>Registration</b> instance. Since the <b>Registration</b> is created using the
    /// <see cref="Lifestyle.Singleton">Singleton</see> lifestyle, both producers will return
    /// the same instance. The <b>InstanceProducer</b> for the <code>Interface1</code> however, will wrap that
    /// instance in a (transient) <code>Interface1Decorator</code>.
    /// <code lang="cs"><![CDATA[
    /// var container = new Container();
    ///
    /// // ServiceImpl implements both Interface1 and Interface2.
    /// var registration = Lifestyle.Singleton.CreateRegistration<ServiceImpl, ServiceImpl>(container);
    ///
    /// var producer1 = new InstanceProducer(typeof(Interface1), registration);
    /// var producer2 = new InstanceProducer(typeof(Interface2), registration);
    ///
    /// container.RegisterDecorator(typeof(Interface1), typeof(Interface1Decorator));
    ///
    /// var instance1 = (Interface1)producer1.GetInstance();
    /// var instance2 = (Interface2)producer2.GetInstance();
    ///
    /// Assert.IsInstanceOfType(instance1, typeof(Interface1Decorator));
    /// Assert.IsInstanceOfType(instance2, typeof(ServiceImpl));
    ///
    /// Assert.AreSame(((Interface1Decorator)instance1).DecoratedInstance, instance2);
    /// ]]></code>
    /// </example>
    [DebuggerTypeProxy(typeof(InstanceProducerDebugView))]
    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ", nq}")]
    public class InstanceProducer
    {
        internal static readonly IEqualityComparer<InstanceProducer> EqualityComparer =
            ReferenceEqualityComparer<InstanceProducer>.Instance;

        private static readonly Predicate<PredicateContext> Always = context => true;

        private readonly object locker = new();
        private readonly LazyEx<Expression> lazyExpression;

        private CyclicDependencyValidator? validator;
        private Func<object> instanceCreator;
        private bool? isValid = true;
        private Lifestyle? overriddenLifestyle;
        private ReadOnlyCollection<KnownRelationship>? knownRelationships;
        private List<Action<Scope>>? verifiers;
        private List<InstanceProducer>? wrappedProducers;

        /// <summary>Initializes a new instance of the <see cref="InstanceProducer"/> class.</summary>
        /// <param name="serviceType">The service type for which this instance is created.</param>
        /// <param name="registration">The <see cref="Registration"/>.</param>
        public InstanceProducer(Type serviceType, Registration registration)
            : this(serviceType, registration, ShouldBeRegisteredAsAnExternalProducer(registration))
        {
        }

        internal InstanceProducer(
            Type serviceType, Registration registration, Predicate<PredicateContext>? predicate)
            : this(serviceType, registration)
        {
            this.Predicate = predicate ?? Always;
        }

        internal InstanceProducer(Type serviceType, Registration registration, bool registerExternalProducer)
        {
            Requires.IsNotNull(serviceType, nameof(serviceType));
            Requires.IsNotNull(registration, nameof(registration));
            Requires.IsNotOpenGenericType(serviceType, nameof(serviceType));
            Requires.ServiceIsAssignableFromImplementation(
                serviceType, registration.ImplementationType, nameof(serviceType));

            this.ServiceType = serviceType;
            this.Registration = registration;
            this.validator = new CyclicDependencyValidator(this);

            this.lazyExpression = new LazyEx<Expression>(this.BuildExpressionInternal);

            if (registerExternalProducer)
            {
                registration.Container.RegisterExternalProducer(this);
            }

            this.instanceCreator = this.BuildAndReplaceInstanceCreatorAndCreateFirstInstance;
            this.ImplementationType = registration.ImplementationType;
        }

        // Flagging the registration with WrapsInstanceCreationDelegate prevents false diagnostic warnings.
        private InstanceProducer(Type serviceType, Expression expression, Container container)
            : this(serviceType,
                  new ExpressionRegistration(expression, container) { WrapsInstanceCreationDelegate = true })
        {
            // Overrides earlier set value. This prevents ExpressionBuilt from being applied.
            this.lazyExpression = Helpers.ToLazy(expression);
        }

        /// <summary>
        /// Gets the <see cref="Lifestyle"/> for this registration. The returned lifestyle can differ from the
        /// lifestyle that is used during the registration. This can happen for instance when the registration
        /// is changed by an <see cref="Container.ExpressionBuilt">ExpressionBuilt</see>
        /// registration or gets decorated.
        /// </summary>
        /// <value>The <see cref="Lifestyle"/> for this registration.</value>
        public Lifestyle Lifestyle => this.overriddenLifestyle ?? this.Registration.Lifestyle;

        /// <summary>Gets the service type for which this producer produces instances.</summary>
        /// <value>A <see cref="Type"/> instance.</value>
        public Type ServiceType { get; }

        /// <summary>Gets the <see cref="Registration"/> instance for this instance.
        /// Please note that, when decorators are applied, this instance is replaces and will become the
        /// registration for the outermost decorator.</summary>
        /// <value>The <see cref="Registration"/>.</value>
        public Registration Registration { get; private set; }

        /// <summary>
        /// Gets the originally registered implementation type. Note that the actual type, returned by
        /// <see cref="GetInstance()"/>, will be different from <see cref="ImplementationType"/> when
        /// decorators or interceptors are applied. This property will always return the originally registered
        /// implementation type. In case the implementation type is unknown, which happens for instance when
        /// registering <see cref="Func{TResult}"/> delegates, the service type is returned.
        /// </summary>
        /// <example>
        /// Given the configuration below, the Assert calls will succeed.
        /// <code lang="cs"><![CDATA[
        /// var container = new Container();
        ///
        /// container.Register<ILogger, ConsoleLogger>();
        /// container.RegisterDecorator<ILogger, LoggerDecorator>();
        ///
        /// var producer = container.GetRegistration(typeof(ILogger));
        ///
        /// Assert.AreEqual(typeof(ConsoleLogger), producer.ImplementationType);
        /// Assert.AreEqual(typeof(ConsoleLogger), producer.Registration.ImplementationType, "Before GetInstance");
        /// Assert.AreEqual(typeof(LoggerDecorator), producer.GetInstance().GetType());
        /// Assert.AreEqual(typeof(LoggerDecorator), producer.Registration.ImplementationType, "After GetInstance");
        /// ]]></code>
        /// </example>
        /// <value>The originally registered implementation type.</value>
        public Type ImplementationType { get; }

        internal Type FinalImplementationType => this.Registration.ImplementationType;

        internal Container Container => this.Registration.Container;

        // Flag that indicates that this type is created by the container (concrete or collection) or resolved
        // using unregistered type resolution.
        internal bool IsContainerAutoRegistered { get; set; }

        // Will only return false when the type is a concrete unregistered type that was automatically added
        // by the container, while the expression can not be generated.
        // Types that are registered upfront are always considered to be valid, while unregistered types must
        // be validated. The reason for this is that we must prevent the container to throw an exception when
        // GetRegistration() is called for an unregistered (concrete) type that can not be resolved.
        internal bool IsValid
        {
            get
            {
                if (this.isValid is null)
                {
                    this.Exception = this.GetExceptionIfInvalid();
                    this.isValid = this.Exception is null;
                }

                return this.isValid.GetValueOrDefault();
            }
        }

        // Gets set by the IsValid and indicates the reason why this producer is invalid. Will be null
        // when the producer is valid.
        internal Exception? Exception { get; private set; }

        internal Predicate<PredicateContext> Predicate { get; } = Always;

        internal bool IsDecorated { get; set; }

        internal bool IsConditional => this.Predicate != Always;

        internal bool IsUnconditional => !this.IsConditional;

        internal bool IsExpressionCreated => this.lazyExpression.IsValueCreated && !this.Registration.MustBeVerified;

        internal bool MustBeExplicitlyVerified => this.verifiers != null;

        internal bool InstanceSuccessfullyCreated { get; private set; }

        internal bool VerifiersAreSuccessfullyCalled { get; private set; }

        internal string DebuggerDisplay => string.Format(
            CultureInfo.InvariantCulture,
            "{0} = {1}, {2} = {3}",
            nameof(this.ServiceType),
            this.ServiceType.ToFriendlyName(),
            nameof(this.Lifestyle),
            this.Lifestyle.Name);

        internal IEnumerable<InstanceProducer> SelfAndWrappedProducers =>
            this.wrappedProducers is null ? this.Self : this.wrappedProducers.Concat(this.Self);

        private IEnumerable<InstanceProducer> Self => new[] { this };

        /// <summary>
        /// Creates a new <see cref="InstanceProducer"/> based on the given <paramref name="serviceType"/>
        /// and <paramref name="expression"/> where the <paramref name="expression"/> will be used as-is;
        /// no interception (using <see cref="Container.ExpressionBuilt">ExpressionBuilt</see>) such as
        /// decorators will be applied.
        /// </summary>
        /// <param name="serviceType">The service type for which this instance is created.</param>
        /// <param name="expression">The expression that describes the instance to be produced.</param>
        /// <param name="container">The <see cref="Container"/> instance for this registration.</param>
        /// <returns>A new <see cref="InstanceProducer"/> that describes the expression.</returns>
        public static InstanceProducer FromExpression(
            Type serviceType, Expression expression, Container container)
        {
            Requires.IsNotNull(serviceType, nameof(serviceType));
            Requires.IsNotNull(expression, nameof(expression));
            Requires.IsNotNull(container, nameof(container));

            return new InstanceProducer(serviceType, expression, container);
        }

        /// <summary>Produces an instance.</summary>
        /// <param name="scope">The scope from which the item must be returned.</param>
        /// <returns>An instance. Will never return null.</returns>
        /// <exception cref="ActivationException">When the instance could not be retrieved or is null.</exception>
        public object GetInstance(Scope scope)
        {
            Requires.IsNotNull(scope, nameof(scope));

            Scope? originalScope = this.Container.CurrentThreadResolveScope;

            try
            {
                this.Container.CurrentThreadResolveScope = scope;
                return this.GetInstance();
            }
            finally
            {
                this.Container.CurrentThreadResolveScope = originalScope;
            }
        }

        /// <summary>Produces an instance.</summary>
        /// <returns>An instance. Will never return null.</returns>
        /// <exception cref="ActivationException">When the instance could not be retrieved or is null.</exception>
        public object GetInstance()
        {
            // We must lock the container, because not locking could lead to race conditions.
            this.Container.LockContainer();

            this.CheckForCyclicDependencies();

            object instance;

            try
            {
                instance = this.instanceCreator();

                // Resolving the instance succeeded; there are no cyclic dependencies. We can remove the
                // validator. This prevents needless checking and increases performance.
                this.RemoveCyclicDependencyValidator();
            }
            catch (Exception ex)
            {
                // We have to clear the counter of the cyclic dependency validator to make sure it will not
                // throw a false positive on the next resolve.
                this.ResetCyclicDependencyValidator();

                if (this.MustWrapThrownException(ex))
                {
                    throw new ActivationException(this.BuildActivationExceptionMessage(ex), ex);
                }

                throw;
            }

            if (instance is null)
            {
                throw new ActivationException(StringResources.DelegateForTypeReturnedNull(this.ServiceType));
            }

            return instance;
        }

        /// <summary>
        /// Builds an expression that expresses the intent to get an instance by the current producer. A call
        /// to this method locks the container. New registrations can't be made after a call to this method.
        /// </summary>
        /// <returns>An Expression.</returns>
        public Expression BuildExpression()
        {
            // We must lock the container, because not locking could lead to race conditions.
            this.Container.LockContainer();

            this.CheckForCyclicDependencies();

            try
            {
                return this.lazyExpression.Value;
            }
            catch (CyclicDependencyException ex)
            {
                // When a cyclic dependency is detected, a CyclicDependencyException will bubble up, and will
                // get enriched by Registration instances with type information about the types in the chain.
                // In case the current producer is the OriginatingProducer, we should transform the exception
                // into an ActivationException to prevent types from being added that are not part of the
                // cycle.
                if (ex.OriginatingProducer == this)
                {
                    throw new ActivationException(ex.Message, ex);
                }

                throw;
            }
            catch (Exception ex)
            {
                if (this.MustWrapThrownException(ex))
                {
                    throw new ActivationException(this.BuildActivationExceptionMessage(ex), ex);
                }

                throw;
            }
            finally
            {
                // NOTE:  We don't remove the cyclic dependency validator while building the expression.
                // Instead we reset it so it can be checked later on again. We do this because only if
                // GetInstance has been called we can know for sure that there's no cyclic dependency. There
                // could be a 'runtime cyclic dependency' caused by a registered delegate that calls back into
                // the container manually. This will not be detected during building the expression, because
                // the delegate won't (always) get executed at this point.
                this.ResetCyclicDependencyValidator();
            }
        }

        /// <summary>
        /// Gets the collection of relationships for this instance that the container knows about.
        /// This includes relationships between the registered type and its dependencies and relationships
        /// between applied decorators and their dependencies. Note that types that are not newed up by the
        /// container and properties that are injected inside a custom delegate that is registered using the
        /// <see cref="Container.RegisterInitializer{TService}">RegisterInitializer</see>
        /// method are unknown to the container and are not returned from this method.
        /// Also note that this method will return an empty collection when called before the
        /// registered type is requested from the container (or before
        /// <see cref="Container.Verify()">Verify</see> is called).
        /// </summary>
        /// <returns>An array of <see cref="KnownRelationship"/> instances.</returns>
        public KnownRelationship[] GetRelationships()
        {
            if (this.knownRelationships != null)
            {
                return this.knownRelationships.ToArray();
            }

            return this.Registration.GetRelationships();
        }

        /// <summary>
        /// Builds a string representation of the object graph with the current instance as root of the
        /// graph.
        /// </summary>
        /// <returns>A string representation of the object graph.</returns>
        /// <exception cref="InvalidOperationException">Thrown when this method is called before
        /// <see cref="GetInstance()"/> or <see cref="BuildExpression"/> have been called. These calls can be
        /// done directly and explicitly by the user on this instance, indirectly by calling
        /// <see cref="GetInstance()"/> or <see cref="BuildExpression"/> on an instance that depends on this
        /// instance, or by calling <see cref="Container.Verify()">Verify</see> on the container.
        /// </exception>
        public string VisualizeObjectGraph() => this.VisualizeObjectGraph(new VisualizationOptions());

        /// <summary>
        /// Builds a string representation of the object graph with the current instance as root of the
        /// graph.
        /// </summary>
        /// <param name="options">The various visualization options for building a string representation of
        /// the object graph.</param>
        /// <returns>A string representation of the object graph.</returns>
        /// <exception cref="InvalidOperationException">Thrown when this method is called before
        /// <see cref="GetInstance()"/> or <see cref="BuildExpression"/> have been called. These calls can be
        /// done directly and explicitly by the user on this instance, indirectly by calling
        /// <see cref="GetInstance()"/> or <see cref="BuildExpression"/> on an instance that depends on this
        /// instance, or by calling <see cref="Container.Verify()">Verify</see> on the container.
        /// </exception>
        /// <exception cref="NullReferenceException">Thrown when options is null.</exception>
        public string VisualizeObjectGraph(VisualizationOptions options)
        {
            Requires.IsNotNull(options, nameof(options));

            if (!this.IsExpressionCreated)
            {
                throw new InvalidOperationException(
                    StringResources.VisualizeObjectGraphShouldBeCalledAfterTheExpressionIsCreated());
            }

            return InstanceProducerVisualizer.VisualizeIndentedObjectGraph(this, options);
        }

        // This method has a side effect: it will ensure the expression is built.
        internal bool ContainsScopedComponentsInGraph() => ContainsScopedComponentsInGraph(this);

        // Throws an InvalidOperationException on failure.
        internal Expression VerifyExpressionBuilding()
        {
            try
            {
                return this.BuildExpression();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    StringResources.ConfigurationInvalidCreatingInstanceFailed(this.ServiceType, ex),
                    ex);
            }
        }

        // Throws an InvalidOperationException on failure.
        internal object VerifyInstanceCreation()
        {
            object instance;

            try
            {
                // Test the creator
                instance = this.GetInstance();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    StringResources.ConfigurationInvalidCreatingInstanceFailed(
                        this.Registration.ImplementationType, ex),
                    ex);
            }

            return instance;
        }

        // A verifier is an Action delegate that will be called during the object creation step in the
        // verification process (when the user calls Verify()) to enable verification of the whole object
        // graph.
        internal void AddVerifier(Action<Scope> action)
        {
            lock (this.locker)
            {
                (this.verifiers ??= new List<Action<Scope>>()).Add(action);
            }
        }

        internal void AddProducerToVerify(InstanceProducer currentProducer)
        {
            lock (this.locker)
            {
                (this.wrappedProducers ??= new List<InstanceProducer>()).Add(currentProducer);
            }
        }

        internal void ReplaceRelationships(IEnumerable<KnownRelationship> relationships)
        {
            this.knownRelationships =
                new ReadOnlyCollection<KnownRelationship>(relationships.Distinct().ToArray());
        }

        internal void EnsureTypeWillBeExplicitlyVerified() => this.isValid = null;

        internal void DoExtraVerfication(Scope scope)
        {
            try
            {
                foreach (var verify in this.GetVerifiers())
                {
                    verify(scope);
                }

                this.VerifiersAreSuccessfullyCalled = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    StringResources.ConfigurationInvalidCreatingInstanceFailed(this.ServiceType, ex),
                    ex);
            }
        }

        private static bool ContainsScopedComponentsInGraph(InstanceProducer producer)
        {
            if (!producer.IsExpressionCreated)
            {
                // We need to build the expressions here, because that could trigger expression built events,
                // which can change the lifestyle of the producer (e.g. because of applied decorators).
                producer.BuildExpression();
            }

            return
                producer.Lifestyle is SingletonLifestyle ? false :
                producer.Lifestyle is ScopedLifestyle ? true :
                producer.GetRelationships().Any(r => ContainsScopedComponentsInGraph(r.Dependency));
        }

        private Action<Scope>[] GetVerifiers()
        {
            lock (this.locker)
            {
                return this.verifiers?.ToArray() ?? Helpers.Array<Action<Scope>>.Empty;
            }
        }

        private Func<object> BuildInstanceCreator()
        {
            // Don't do recursive checks. The GetInstance() already does that.
            var expression = this.lazyExpression.Value;

            Func<object> creator;

            try
            {
                creator = CompilationHelpers.CompileExpression<object>(this.Container, expression);
                creator = this.Container.WrapWithResolveInterceptor(this, creator);
            }
            catch (Exception ex)
            {
                string message =
                    StringResources.ErrorWhileBuildingDelegateFromExpression(this.ServiceType, expression, ex);

                throw new ActivationException(message, ex);
            }

            this.Analyze();

            return creator;
        }

        private void Analyze()
        {
            if (!this.Container.Options.SuppressLifestyleMismatchVerification)
            {
                var error = new LifestyleMismatchAnalyzer().Analyze(this.SelfAndWrappedProducers)
                    .Cast<LifestyleMismatchDiagnosticResult>()
                    .FirstOrDefault();

                if (error != null)
                {
                    throw new DiagnosticVerificationException(
                        StringResources.LifestyleMismatchesReported(error), error);
                }
            }
        }

        private Expression BuildExpressionInternal()
        {
            var expression = this.Registration.BuildExpression();

            if (expression is null)
            {
                throw new ActivationException(StringResources.RegistrationReturnedNullFromBuildExpression(
                    this.Registration));
            }

            ExpressionBuiltEventArgs? e = this.Container.OnExpressionBuilt(this, expression);

            if (e != null)
            {
                if (!object.ReferenceEquals(this.Registration, e.ReplacedRegistration))
                {
                    this.Registration = e.ReplacedRegistration;
                }
                else
                {
                    this.overriddenLifestyle = e.Lifestyle;
                }

                this.Analyze();

                return e.Expression;
            }
            else
            {
                this.overriddenLifestyle = this.Lifestyle;

                this.Analyze();

                return expression;
            }
        }

        private bool MustWrapThrownException(Exception ex)
        {
            return this.IsContainerAutoRegistered
                || this.Registration.WrapsInstanceCreationDelegate
                || !(ex is ActivationException);
        }

        private string BuildActivationExceptionMessage(Exception innerException)
        {
            if (this.IsContainerAutoRegistered)
            {
                return StringResources.ImplicitRegistrationCouldNotBeMadeForType(this.ServiceType,
                    this.Container.HasRegistrations) + " " + innerException.Message;
            }
            else if (this.Registration.WrapsInstanceCreationDelegate)
            {
                return StringResources.DelegateForTypeThrewAnException(this.ServiceType) + " " +
                    innerException.Message;
            }
            else
            {
                return innerException.Message;
            }
        }

        // This method allows the GetInstance method to be optimized by making removing one null check in
        // the library's happy path.
        private object BuildAndReplaceInstanceCreatorAndCreateFirstInstance()
        {
            this.instanceCreator = this.BuildInstanceCreator();

            var instance = this.instanceCreator();
            this.InstanceSuccessfullyCreated = true;
            return instance;
        }

        // Prevents any recursive calls from taking place.
        // This method will be inlined by the JIT.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void CheckForCyclicDependencies()
        {
            this.validator?.Check();
        }

        // This method will be inlined by the JIT.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void RemoveCyclicDependencyValidator()
        {
            // No recursive calls detected, we can remove the validator to increase performance.
            // We first check for null, because this is faster. Every time we write, the CPU has to send
            // the new value to all the other CPUs. We only nullify the validator while using the GetInstance
            // method, because the BuildExpression will only be called a limited amount of time.
            if (this.validator != null)
            {
                this.validator = null;
            }
        }

        // This method will be inlined by the JIT.
        // Resets the validator to its initial state. This is important when a IInstanceProvider threw an
        // exception, because a new call to that provider would otherwise make the validator think it is a
        // recursive call and throw an exception, and this would hide the exception that would otherwise be
        // thrown by the provider itself.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void ResetCyclicDependencyValidator()
        {
            this.validator?.Reset();
        }

        private Exception? GetExceptionIfInvalid()
        {
            try
            {
                // Test if the instance can be made.
                this.BuildExpression();

                return null;
            }
            catch (ActivationException ex)
            {
                return ex.InnerException ?? ex;
            }
        }

        private static bool ShouldBeRegisteredAsAnExternalProducer(Registration registration)
        {
            // ExpressionRegistration is an internal Registration type. An InstanceProducer with this type
            // of registration doesn't have to be registered, since it will either always be registered
            // in the registrations dictionary anyway, or it is used to build up an InstanceProducer (by
            // the decorator sub system) that is only used for diagnostics. Allowing the latter producers to
            // be added, will clutter the diagnostic API and will cause the Verify() method to verify those
            // producers needlessly.
            return !(registration is ExpressionRegistration);
        }

        [ExcludeFromCodeCoverage]
        internal sealed class InstanceProducerDebugView
        {
            private readonly InstanceProducer producer;

            internal InstanceProducerDebugView(InstanceProducer producer)
            {
                this.producer = producer;
            }

            public Lifestyle Lifestyle => this.producer.Lifestyle;

            [DebuggerDisplay("{" + TypesExtensions.FriendlyName + "(" + nameof(ServiceType) + "), nq}")]
            public Type ServiceType => this.producer.ServiceType;

            [DebuggerDisplay("{" + TypesExtensions.FriendlyName + "(" + nameof(ImplementationType) + "), nq}")]
            public Type ImplementationType => this.producer.FinalImplementationType;

            public KnownRelationship[] Relationships => this.producer.GetRelationships();

            // By using the TruncatedDependencyGraph as value of this DependencyGraph property, we allow the
            // graph to be shown in compact form in the debugger in-line value field, but still allow the
            // complete formatted object graph to be shown when the user opens the text visualizer.
            [DebuggerDisplay(value: "{" + nameof(TruncatedDependencyGraph) + ", nq}")]
            public string DependencyGraph => this.producer.VisualizeIndentedObjectGraph(new VisualizationOptions());

            [DebuggerHidden]
            private string TruncatedDependencyGraph => this.producer.VisualizeInlinedAndTruncatedObjectGraph(160);
        }
    }
}