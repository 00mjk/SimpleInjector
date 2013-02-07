﻿#region Copyright (c) 2010 S. van Deursen
/* The Simple Injector is an easy-to-use Inversion of Control library for .NET
 * 
 * Copyright (C) 2010 S. van Deursen
 * 
 * To contact me, please visit my blog at http://www.cuttingedge.it/blogs/steven/ or mail to steven at 
 * cuttingedge.it.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
 * associated documentation files (the "Software"), to deal in the Software without restriction, including 
 * without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 * copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the 
 * following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all copies or substantial 
 * portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
 * LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO 
 * EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER 
 * IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE 
 * USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
#endregion

namespace SimpleInjector
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    using SimpleInjector.Advanced;
    using SimpleInjector.Extensions;
    using SimpleInjector.Extensions.Decorators;

    internal static class Requires
    {
        private static readonly Type[] AmbiguousTypes = new[] { typeof(Type), typeof(string) };

        internal static void IsNotNull(object instance, string paramName)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(paramName);
            }
        }

        internal static void IsNotNullOrEmpty(string instance, string paramName)
        {
            IsNotNull(instance, paramName);

            if (instance.Length == 0)
            {
                throw new ArgumentException("Value can not be empty.", paramName);
            }
        }

        internal static void IsReferenceType(Type type, string paramName)
        {
            if (!type.IsClass && !type.IsInterface)
            {
                throw new ArgumentException(StringResources.SuppliedTypeIsNotAReferenceType(type), paramName);
            }
        }

        internal static void IsNotOpenGenericType(Type type, string paramName)
        {
            // We check for ContainsGenericParameters to see whether there is a Generic Parameter 
            // to find out if this type can be created.
            if (type.ContainsGenericParameters)
            {
                throw new ArgumentException(StringResources.SuppliedTypeIsAnOpenGenericType(type), paramName);
            }
        }

        internal static void ServiceIsAssignableFromImplementation(Type service, Type implementation,
            string paramName)
        {
            if (!service.IsAssignableFrom(implementation))
            {
                throw new ArgumentException(
                    StringResources.SuppliedTypeDoesNotInheritFromOrImplement(service, implementation),
                    paramName);
            }
        }

        internal static void IsNotAnAmbiguousType(Type type, string paramName)
        {
            if (AmbiguousTypes.Contains(type))
            {
                throw new ArgumentException(StringResources.TypeIsAmbiguous(type), paramName);
            }
        }

        internal static void TypeIsOpenGeneric(Type type, string paramName)
        {
            // We don't check for ContainsGenericParameters, because we can't handle types that don't have
            // a direct parameter (such as Lazy<Func<TResult>>). This is a limitation in the current
            // implementation of the GenericArgumentFinder. That's not an easy thing to fix :-(
            if (!type.IsGenericTypeDefinition)
            {
                string message = StringResources.SuppliedTypeIsNotAnOpenGenericType(type);

                throw new ArgumentException(message, paramName);
            }
        }

        internal static void DoesNotContainNullValues<T>(IEnumerable<T> collection, string paramName)
            where T : class
        {
            if (collection != null && collection.Contains(null))
            {
                throw new ArgumentException("The collection contains null elements.", paramName);
            }
        }

        internal static void TypeIsNotOpenGeneric(Type type, string paramName)
        {
            // We check for ContainsGenericParameters to see whether there is a Generic Parameter 
            // to find out if this type can be created.
            if (type.ContainsGenericParameters)
            {
                throw new ArgumentException(StringResources.SuppliedTypeIsAnOpenGenericType(type), paramName);
            }
        }

        internal static void TypeIsReferenceType(Type type, string paramName)
        {
            if (!type.IsClass && !type.IsInterface)
            {
                throw new ArgumentException(StringResources.SuppliedTypeIsNotAReferenceType(type), paramName);
            }
        }

        internal static void DoesNotContainOpenGenericTypes(IEnumerable<Type> serviceTypes, string paramName)
        {
            foreach (var type in serviceTypes)
            {
                TypeIsNotOpenGeneric(type, paramName);
            }
        }

        internal static void ServiceTypeIsNotClosedWhenImplementationIsOpen(Type service, Type implementation)
        {
            if (service.IsGenericType && !service.ContainsGenericParameters &&
                implementation.IsGenericType && implementation.ContainsGenericParameters)
            {
                throw new NotSupportedException(
                    StringResources.SuppliedTypeCanNotBeOpenWhenDecoratorIsClosed());
            }
        }

        internal static void ServiceOrItsGenericTypeDefinitionIsAssignableFromImplementation(Type service, 
            Type implementation, string paramName)
        {
            if (service != implementation &&
                !ExtensionHelpers.ServiceIsAssignableFromImplementation(service, implementation))
            {
                throw new ArgumentException(
                    StringResources.SuppliedTypeDoesNotInheritFromOrImplement(service, implementation),
                    paramName);
            }
        }

        internal static void ServiceIsAssignableFromImplementations(Type serviceType,
            IEnumerable<Type> typesToRegister, string paramName, bool typeCanBeServiceType = false)
        {
            var invalidType = (
                from type in typesToRegister
                where !ExtensionHelpers.ServiceIsAssignableFromImplementation(serviceType, type)
                where !typeCanBeServiceType || type != serviceType
                select type).FirstOrDefault();

            if (invalidType != null)
            {
                throw new ArgumentException(
                    StringResources.SuppliedTypeDoesNotInheritFromOrImplement(serviceType, invalidType),
                    paramName);
            }
        }

        internal static void ImplementationHasSelectableConstructor(Container container, Type serviceType,
            Type implementationType, string paramName)
        {
            string message;

            if (!container.IsConstructableType(serviceType, implementationType, out message))
            {
                throw new ArgumentException(message, paramName);
            }
        }

        internal static void DecoratorDoesNotContainUnresolvableTypeArguments(Type serviceType,
            Type decoratorType, string parameterName)
        {
            if (serviceType.ContainsGenericParameters && decoratorType.ContainsGenericParameters)
            {
                var builder = new GenericTypeBuilder(serviceType, decoratorType);

                if (!builder.ClosedServiceTypeSatisfiesAllTypeConstraints())
                {
                    string error = StringResources.DecoratorContainsUnresolvableTypeArguments(decoratorType);

                    throw new ArgumentException(error, parameterName);
                }
            }
        }

        internal static void DecoratorIsNotAnOpenGenericTypeDefinitionWhenTheServiceTypeIsNot(Type serviceType,
            Type decoratorType, string parameterName)
        {
            if (!serviceType.ContainsGenericParameters && decoratorType.ContainsGenericParameters)
            {
                throw new ArgumentException(
                    StringResources.DecoratorCanNotBeAGenericTypeDefinitionWhenServiceTypeIsNot(
                        serviceType, decoratorType), parameterName);
            }
        }

        internal static void IsValidValue(AccessibilityOption accessibility, string paramName)
        {
            if (accessibility != AccessibilityOption.AllTypes &&
                accessibility != AccessibilityOption.PublicTypesOnly)
            {
                throw new ArgumentException(
                    StringResources.ValueIsInvalidForEnumType((int)accessibility, typeof(AccessibilityOption)),
                    paramName);
            }
        }

        internal static void IsDecorator(Container container, Type serviceType, Type decoratorType,
            string paramName)
        {
            ConstructorInfo decoratorConstructor = 
                container.Options.ConstructorResolutionBehavior.GetConstructor(serviceType, decoratorType);

            Requires.DecoratesServiceType(serviceType, decoratorConstructor, paramName);
            Requires.DecoratesBaseTypes(serviceType, decoratorConstructor, paramName);
        }

        private static void DecoratesServiceType(Type serviceType, ConstructorInfo decoratorConstructor, 
            string paramName)
        {
            bool decoratesServiceType = DecoratorHelpers.DecoratesServiceType(serviceType, decoratorConstructor);

            if (!decoratesServiceType)
            {
                ThrowMustDecorateServiceType(serviceType, decoratorConstructor, paramName);
            }
        }

        private static void ThrowMustDecorateServiceType(Type serviceType, 
            ConstructorInfo decoratorConstructor, string paramName)
        {
            int numberOfServiceTypeDependencies =
                DecoratorHelpers.GetNumberOfServiceTypeDependencies(serviceType, decoratorConstructor);

            string message = StringResources.TheConstructorOfTypeMustContainTheServiceTypeAsArgument(
                decoratorConstructor.DeclaringType, serviceType, numberOfServiceTypeDependencies);

            throw new ArgumentException(message, paramName);
        }
        
        private static void DecoratesBaseTypes(Type serviceType, ConstructorInfo decoratorConstructor, 
            string paramName)
        {
            bool decoratesBaseTypes = 
                DecoratorHelpers.DecoratesBaseTypes(serviceType, decoratorConstructor);

            if (!decoratesBaseTypes)
            {
                ThrowMustDecorateBaseType(serviceType, decoratorConstructor.DeclaringType, paramName);
            }
        }

        private static void ThrowMustDecorateBaseType(Type serviceType, Type decoratorType, string paramName)
        {
            var validConstructorArgumentTypes =
                DecoratorHelpers.GetValidDecoratorConstructorArgumentTypes(serviceType, decoratorType);

            string message = StringResources.TheConstructorOfTypeMustContainTheServiceTypeAsArgument(
                decoratorType, validConstructorArgumentTypes);

            throw new ArgumentException(message, paramName);
        }
    }
}