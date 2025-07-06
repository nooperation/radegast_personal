using OpenMetaverse;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Radegast
{
    internal class CompositeCOFPolicy : ICOFPolicy
    {
        private readonly object _policiesLock = new object();
        private ImmutableHashSet<ICOFPolicy> _policies = ImmutableHashSet<ICOFPolicy>.Empty;

        private ImmutableHashSet<ICOFPolicy> GetCurrentPolicies()
        {
            lock (_policiesLock)
            {
                return _policies;
            }
        }

        public CompositeCOFPolicy AddPolicy(ICOFPolicy policyToAdd)
        {
            if (policyToAdd == null)
            {
                throw new ArgumentNullException(nameof(policyToAdd));
            }

            lock (_policiesLock)
            {
                _policies = _policies.Add(policyToAdd);
            }

            return this;
        }

        public void RemovePolicy(ICOFPolicy policyToRemove)
        {
            lock (_policiesLock)
            {
                _policies = _policies.Remove(policyToRemove);
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
