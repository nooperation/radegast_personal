using OpenMetaverse;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Radegast
{
    internal class CompositeCOFPolicy : ICOFPolicy
    {
        private readonly object policiesLock = new object();
        private ImmutableHashSet<ICOFPolicy> policies = ImmutableHashSet<ICOFPolicy>.Empty;

        private ImmutableHashSet<ICOFPolicy> GetCurrentPolicies()
        {
            lock (policiesLock)
            {
                return policies;
            }
        }

        public CompositeCOFPolicy AddPolicy(ICOFPolicy policyToAdd)
        {
            if (policyToAdd == null)
            {
                throw new ArgumentNullException(nameof(policyToAdd));
            }

            lock (policiesLock)
            {
                policies = policies.Add(policyToAdd);
            }

            return this;
        }

        public void RemovePolicy(ICOFPolicy policyToRemove)
        {
            lock (policiesLock)
            {
                policies = policies.Remove(policyToRemove);
            }
        }

        public bool CanAttach(InventoryItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            return GetCurrentPolicies()
                .All(n => n.CanAttach(item));
        }

        public bool CanDetach(InventoryItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            return GetCurrentPolicies()
                .All(n => n.CanDetach(item));
        }
    }
}
