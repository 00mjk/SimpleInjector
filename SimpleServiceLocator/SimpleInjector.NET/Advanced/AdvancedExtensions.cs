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

namespace SimpleInjector.Advanced
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Reflection;

    /// <summary>
    /// Extension methods for enable advanced scenarios.
    /// </summary>
    public static class AdvancedExtensions
    {
        /// <summary>
        /// Determines whether the specified container is locked making any new registrations. The container
        /// is automatically locked when <see cref="Container.GetInstance">GetInstance</see> is called for the
        /// first time.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <returns>
        ///   <c>true</c> if the specified container is locked; otherwise, <c>false</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="container"/> is null.</exception>
        public static bool IsLocked(this Container container)
        {
            Requires.IsNotNull(container, "container");

            return container.IsLocked;
        }

        /// <summary>Determines whether the specified container is currently verifying its configuration.</summary>
        /// <param name="container">The container.</param>
        /// <returns><c>true</c> if the specified container is verifying; otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="container"/> is null.</exception>
        public static bool IsVerifying(this Container container)
        {
            Requires.IsNotNull(container, "container");

            return container.IsVerifying;
        }

        /// <summary>
        /// Builds up an <see cref="Action{T}"/> delegate wrapping all <see cref="Action{T}"/> delegates that
        /// are registered using <see cref="Container.RegisterInitializer{T}">RegisterInitializer</see> and
        /// that apply to the given <typeparamref name="TService"/> (including delegates that are registered
        /// for interfaces <typeparamref name="TService"/> implements and base types that 
        /// <typeparamref name="TService"/> inherits from). <b>Null</b> will be returned when no delegates are
        /// registered that apply to this type.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <remarks>
        /// This method has a performance caracteristic of O(n). Prevent from calling this in a performance
        /// critical path of the application.
        /// </remarks>
        /// <typeparam name="TService">The type for with an initializer must be built.</typeparam>
        /// <returns>An <see cref="Action{TService}"/> delegate or <b>null</b>.</returns>
        [SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter",
            Justification = "We need to return a Action<TService> and we therefore need the generic type param.")]
        public static Action<TService> GetInitializer<TService>(this Container container)
        {
            Requires.IsNotNull(container, "container");

            return container.GetInitializer<TService>();
        }

        internal static void Verify(this IConstructorVerificationBehavior behavior, ConstructorInfo constructor)
        {
            foreach (var parameter in constructor.GetParameters())
            {
                behavior.Verify(parameter);
            }
        }
    }
}