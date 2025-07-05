/*
 * Radegast Metaverse Client
 * Copyright(c) 2009-2014, Radegast Development Team
 * Copyright(c) 2016-2025, Sjofn, LLC
 * All rights reserved.
 *  
 * Radegast is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program.If not, see<https://www.gnu.org/licenses/>.
 */

using OpenMetaverse;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Radegast
{
    public class CurrentOutfitFolder : IDisposable
    {
        #region Fields

        private GridClient Client;
        private readonly RadegastInstance Instance;
        private bool InitializedCOF = false;
        public InventoryFolder COF;

        public int MaxClothingLayers => 60;

        #endregion Fields

        #region Construction and disposal
        public CurrentOutfitFolder(RadegastInstance instance)
        {
            Instance = instance;
            Client = instance.Client;
            Instance.ClientChanged += instance_ClientChanged;
            RegisterClientEvents(Client);
        }

        public void Dispose()
        {
            UnregisterClientEvents(Client);
            Instance.ClientChanged -= instance_ClientChanged;
        }
        #endregion Construction and disposal

        #region Event handling

        private void instance_ClientChanged(object sender, ClientChangedEventArgs e)
        {
            UnregisterClientEvents(Client);
            Client = e.Client;
            RegisterClientEvents(Client);
        }

        private void RegisterClientEvents(GridClient client)
        {
            client.Network.SimChanged += Network_OnSimChanged;
            client.Inventory.FolderUpdated += Inventory_FolderUpdated;
            client.Objects.KillObject += Objects_KillObject;
        }

        private void UnregisterClientEvents(GridClient client)
        {
            client.Network.SimChanged -= Network_OnSimChanged;
            client.Inventory.FolderUpdated -= Inventory_FolderUpdated;
            client.Objects.KillObject -= Objects_KillObject;

            InitializedCOF = false;
        }

        private void Inventory_FolderUpdated(object sender, FolderUpdatedEventArgs e)
        {
            if (COF == null)
            {
                return;
            }

            if (e.FolderID == COF.UUID && e.Success)
            {
                if (Client.Inventory.Store.TryGetValue<InventoryFolder>(COF.UUID, out var newCOF))
                {
                    // Sometimes we will need to update our COF reference, such as when we clear
                    //   and re-fetch our Inventory.Store
                    COF = newCOF;
                }

                var cofLinks = GetCurrentOutfitLinks().Result;

                var items = new Dictionary<UUID, UUID>();
                foreach (var link in cofLinks)
                {
                    items[link.AssetUUID] = Client.Self.AgentID;
                }

                if (items.Count > 0)
                {
                    Client.Inventory.RequestFetchInventory(items);
                }
            }
        }

        private void Objects_KillObject(object sender, KillObjectEventArgs e)
        {
            if (Client.Network.CurrentSim != e.Simulator)
            {
                return;
            }

            if (Client.Network.CurrentSim.ObjectsPrimitives.TryGetValue(e.ObjectLocalID, out var prim))
            {
                var invItem = CurrentOutfitFolder.GetAttachmentItemID(prim);
                if (invItem != UUID.Zero)
                {
                    RemoveLink(invItem).Wait();
                }
            }
        }

        private void Network_OnSimChanged(object sender, SimChangedEventArgs e)
        {
            Client.Network.CurrentSim.Caps.CapabilitiesReceived += Simulator_OnCapabilitiesReceived;
        }

        private void Simulator_OnCapabilitiesReceived(object sender, CapabilitiesReceivedEventArgs e)
        {
            e.Simulator.Caps.CapabilitiesReceived -= Simulator_OnCapabilitiesReceived;

            if (e.Simulator == Client.Network.CurrentSim && !InitializedCOF)
            {
                InitializeCurrentOutfitFolder().Wait();
            }
        }

        #endregion Event handling

        #region Private methods

        private async Task<bool> InitializeCurrentOutfitFolder(CancellationToken cancellationToken = default)
        {
            COF = await Client.Appearance.GetCurrentOutfitFolder(cancellationToken);

            if (COF == null)
            {
                //CreateCurrentOutfitFolder();
            }
            else
            {
                await Client.Inventory.RequestFolderContents(COF.UUID, Client.Self.AgentID,
                    true, true, InventorySortOrder.ByDate, cancellationToken);
            }

            Logger.Log($"Initialized Current Outfit Folder with UUID {COF.UUID} v.{COF.Version}", Helpers.LogLevel.Info, Client);

            InitializedCOF = COF != null;
            return InitializedCOF;
        }

        private void CreateCurrentOutfitFolder()
        {
            UUID cofId = Client.Inventory.CreateFolder(Client.Inventory.Store.RootFolder.UUID,
                "Current Outfit", FolderType.CurrentOutfit);
            if (Client.Inventory.Store.Contains(cofId) && Client.Inventory.Store[cofId] is InventoryFolder folder)
            {
                COF = folder;
            }
        }

        private bool IsBodyPart(InventoryItem item)
        {
            var realItem = Instance.COF.ResolveInventoryLink(item);
            if (realItem == null)
            {
                return false;
            }

            if (!(realItem is InventoryWearable wearable))
            {
                return false;
            }

            return wearable.WearableType == WearableType.Shape ||
                   wearable.WearableType == WearableType.Skin ||
                   wearable.WearableType == WearableType.Eyes ||
                   wearable.WearableType == WearableType.Hair;
        }

        /// <summary>
        /// Return links found in Current Outfit Folder
        /// </summary>
        /// <returns>List of <see cref="InventoryItem"/> that can be part of appearance (attachments, wearables)</returns>
        private async Task<List<InventoryItem>> GetCurrentOutfitLinks(CancellationToken cancellationToken = default)
        {
            if (COF == null)
            {
                await InitializeCurrentOutfitFolder(cancellationToken);
            }

            if (COF == null)
            {
                Logger.Log($"COF is null", Helpers.LogLevel.Warning, Client);
                return new List<InventoryItem>();
            }

            if (!Client.Inventory.Store.TryGetNodeFor(COF.UUID, out var cofNode))
            {
                Logger.Log($"Failed to find COF node in inventory store", Helpers.LogLevel.Warning, Client);
                return new List<InventoryItem>();
            }

            List<InventoryBase> cofContents;
            if (cofNode.NeedsUpdate)
            {
                cofContents = await Client.Inventory.RequestFolderContents(
                    COF.UUID,
                    COF.OwnerID,
                    true,
                    true,
                    InventorySortOrder.ByName,
                    cancellationToken
                );
            }
            else
            {
                cofContents = Client.Inventory.Store.GetContents(COF);
            }

            var cofLinks = cofContents.OfType<InventoryItem>()
                .Where(n => n.IsLink())
                .ToList();

            return cofLinks;
        }

        /// <summary>
        /// Creates a new COF link
        /// </summary>
        /// <param name="item">Original item to be linked from COF</param>
        private async Task AddLink(InventoryItem item, CancellationToken cancellationToken = default)
        {
            if (item is InventoryWearable wearableItem && !IsBodyPart(item))
            {
                var layer = 0;
                var desc = $"{(int)wearableItem.WearableType}{layer:00}";
                await AddLink(item, desc, cancellationToken);
            }
            else
            {
                await AddLink(item, string.Empty, cancellationToken);
            }
        }

        /// <summary>
        /// Creates a new COF link
        /// </summary>
        /// <param name="item">Original item to be linked from COF</param>
        /// <param name="newDescription">Description for the link</param>
        private async Task AddLink(InventoryItem item, string newDescription, CancellationToken cancellationToken = default)
        {
            if (COF == null)
            {
                Logger.Log("Can't add link; COF hasn't been initialized.", Helpers.LogLevel.Warning, Client);
                return;
            }

            var cofLinks = await GetCurrentOutfitLinks(cancellationToken);
            if (cofLinks.Find(itemLink => itemLink.AssetUUID == item.UUID) == null)
            {
                Client.Inventory.CreateLink(
                    COF.UUID,
                    item.UUID,
                    item.Name,
                    newDescription,
                    item.InventoryType,
                    UUID.Random(),
                    (success, newItem) =>
                    {
                        if (success)
                        {
                            Client.Inventory.RequestFetchInventory(newItem.UUID, newItem.OwnerID);
                        }
                    },
                    cancellationToken
                );
            }
        }

        /// <summary>
        /// Removes all COF links to the specified actual item ID
        /// </summary>
        /// <param name="itemID">Actual item ID of the inventory item we want to remove COF links to</param>
        /// <param name="cancellationToken"></param>
        private async Task RemoveLink(UUID itemID, CancellationToken cancellationToken = default)
        {
            await RemoveLinks(new List<UUID>(1) { itemID }, cancellationToken);
        }

        /// <summary>
        /// Removes all COF links to the specified item ID's
        /// </summary>
        /// <param name="itemIDsToRemove">List of actual item ID's we want to removel COF links to</param>
        /// <param name="cancellationToken"></param>
        private async Task RemoveLinks(List<UUID> itemIDsToRemove, CancellationToken cancellationToken = default)
        {
            if (COF == null)
            {
                Logger.Log("Can't remove link; COF hasn't been initialized.", Helpers.LogLevel.Warning, Client);
                return;
            }

            var cofLinks = await GetCurrentOutfitLinks(cancellationToken);

            var itemIDsToRemoveSet = itemIDsToRemove.ToHashSet();
            var linkIdsToRemove = cofLinks
                .Where(n => n.IsLink() && itemIDsToRemoveSet.Contains(n.AssetUUID))
                .Select(n => n.UUID)
                .Distinct()
                .ToList();

            await Client.Inventory.RemoveItemsAsync(linkIdsToRemove, cancellationToken);
        }

        #endregion Private methods

        #region Public methods

        /// <summary>
        /// Determines if we can attach the specified object
        /// </summary>
        /// <param name="item">Object to check</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True if we are able to attach this object</returns>
        public async Task<bool> CanAttachItem(InventoryItem item, CancellationToken cancellationToken = default)
        {
            if (!(item is InventoryObject))
            {
                return false;
            }

            var trashFolderId = Client.Inventory.FindFolderForType(FolderType.Trash);
            var rootFolderId = Client.Inventory.FindFolderForType(FolderType.Root);

            var realItem = Instance.COF.ResolveInventoryLink(item);
            if (realItem == null)
            {
                Logger.Log($"Cannot attach an item because the link could not be resolved.", Helpers.LogLevel.Warning, Client);
                return false;
            }

            var isInTrash = await Instance.COF.IsObjectDescendentOf(realItem, trashFolderId, cancellationToken);
            if (isInTrash)
            {
                Logger.Log($"Cannot attach an item that is currently in the trash.", Helpers.LogLevel.Warning, Client);
                return false;
            }

            var isInPlayerInventory = await Instance.COF.IsObjectDescendentOf(realItem, rootFolderId, cancellationToken);
            if (!isInPlayerInventory)
            {
                Logger.Log($"Cannot attach an item that is not in your inventory.", Helpers.LogLevel.Warning, Client);
                return false;
            }

            var cofLinks = await GetCurrentOutfitLinks(cancellationToken);
            var numAttachedObjects = cofLinks
                .Count(n => n is InventoryObject);

            if (numAttachedObjects + 1 >= Client.Self.Benefits.AttachmentLimit)
            {
                Logger.Log($"Cannot attach any more objects. Maximum of {Client.Self.Benefits.AttachmentLimit} attached objects has been reached", Helpers.LogLevel.Warning, Client);
                return false;
            }

            if (cofLinks.FirstOrDefault(n => n.ActualUUID == item.ActualUUID) != null)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Attempt to attach an object to a specific attachment point
        /// </summary>
        /// <param name="item">Item to be attached</param>
        /// <param name="point">Attachment point</param>
        /// <param name="replace">Replace existing attachment at that point first?</param>
        /// <param name="cancellationToken"></param>
        public async Task Attach(InventoryItem item, AttachmentPoint point, bool replace, CancellationToken cancellationToken = default)
        {
            if (!await CanAttachItem(item, cancellationToken))
            {
                return;
            }

            Client.Appearance.Attach(item, point, replace);
            await AddLink(item, cancellationToken);
        }

        /// <summary>
        /// Remove attachment
        /// </summary>
        /// <param name="item">Inventory item to be detached</param>
        public async Task Detach(InventoryItem item, CancellationToken cancellationToken = default)
        {
            var realItem = Instance.COF.ResolveInventoryLink(item);
            if (realItem == null)
            {
                return;
            }

            if (IsBodyPart(realItem))
            {
                return;
            }

            Client.Appearance.Detach(item);
            await RemoveLink(item.UUID, cancellationToken);
        }

        /// <summary>
        /// Gets a list of worn items of a specific wearable type
        /// </summary>
        /// <param name="type">Specific wearable type to find</param>
        /// <param name="cancellationToken"></param>
        /// <returns>List of all worn items of the specified wearable type</returns>
        public async Task<List<InventoryItem>> GetWornAt(WearableType type, CancellationToken cancellationToken = default)
        {
            var wornItemsByAssetId = new Dictionary<UUID, InventoryItem>();

            var cofLinks = await GetCurrentOutfitLinks(cancellationToken);
            foreach (var link in cofLinks)
            {
                var realItem = Instance.COF.ResolveInventoryLink(link);
                if (realItem == null)
                {
                    continue;
                }

                if (!(realItem is InventoryWearable wearable))
                {
                    continue;
                }

                if (wearable.WearableType == type)
                {
                    wornItemsByAssetId[wearable.AssetUUID] = wearable;
                }
            }

            return wornItemsByAssetId.Values.ToList();
        }

        /// <summary>
        /// Replaces the current outfit and updates COF links accordingly
        /// </summary>
        /// <param name="newOutfit">List of new wearables and attachments that comprise the new outfit</param>
        public async Task<bool> ReplaceOutfit(UUID newOutfitFolderId, CancellationToken cancellationToken = default)
        {
            // TODO: Copy from library if necessary

            const string generalErrorMessage = "Try refreshing your inventory or clearing your cache.";

            var trashFolderId = Client.Inventory.FindFolderForType(FolderType.Trash);
            var rootFolderId = Client.Inventory.Store.RootFolder.UUID;

            var newOutfit = await Client.Inventory.RequestFolderContents(
                newOutfitFolderId,
                Client.Self.AgentID,
                true,
                true,
                InventorySortOrder.ByName,
                cancellationToken
            );
            if (newOutfit == null)
            {
                Logger.Log($"Failed to request contents of replacement outfit folder. {generalErrorMessage}", Helpers.LogLevel.Warning, Client);
                return false;
            }

            if (!Client.Inventory.Store.TryGetNodeFor(newOutfitFolderId, out var newOutfitFolderNode))
            {
                Logger.Log($"Failed to get node for replacement outfit folder. {generalErrorMessage}", Helpers.LogLevel.Warning, Client);
                return false;
            }

            var isOutfitInTrash = await Instance.COF.IsObjectDescendentOf(newOutfitFolderNode.Data, trashFolderId, cancellationToken);
            if (isOutfitInTrash)
            {
                Logger.Log($"Cannot wear an outfit that is currently in the trash.", Helpers.LogLevel.Warning, Client);
                return false;
            }

            var isOutfitInInventory = await Instance.COF.IsObjectDescendentOf(newOutfitFolderNode.Data, rootFolderId, cancellationToken);
            if (!isOutfitInInventory)
            {
                Logger.Log($"Cannot wear an outfit that is not currently in your inventory.", Helpers.LogLevel.Warning, Client);
                return false;
            }

            var currentOutfitFolder = await Client.Appearance.GetCurrentOutfitFolder(cancellationToken);
            if (currentOutfitFolder == null)
            {
                Logger.Log($"Failed to find current outfit folder. {generalErrorMessage}", Helpers.LogLevel.Warning, Client);
                return false;
            }

            var currentOutfitContents = await Client.Inventory.RequestFolderContents(
                currentOutfitFolder.UUID,
                currentOutfitFolder.OwnerID,
                true,
                true,
                InventorySortOrder.ByName,
                cancellationToken
            );
            if (currentOutfitContents == null)
            {
                Logger.Log($"Failed to request contents of current outfit folder. {generalErrorMessage}", Helpers.LogLevel.Warning, Client);
                return false;
            }

            var itemsToWear = new Dictionary<UUID, InventoryItem>();
            var existingBodypartLinks = new List<InventoryItem>();
            var bodypartsToWear = new Dictionary<WearableType, InventoryWearable>();
            var gesturesToActivate = new Dictionary<UUID, InventoryItem>();
            var numClothingLayers = 0;
            var numAttachedObjects = 0;

            foreach (var item in newOutfit)
            {
                if (!(item is InventoryItem inventoryItem))
                {
                    continue;
                }

                if (inventoryItem.IsLink())
                {
                    continue;
                }

                var isInTrash = await Instance.COF.IsObjectDescendentOf(inventoryItem, trashFolderId, cancellationToken);
                if (isInTrash)
                {
                    continue;
                }

                var isInInventory = await Instance.COF.IsObjectDescendentOf(inventoryItem, rootFolderId, cancellationToken);
                if (!isInInventory)
                {
                    continue;
                }

                if (inventoryItem.AssetType == AssetType.Bodypart)
                {
                    if (!(item is InventoryWearable bodypartItem))
                    {
                        continue;
                    }

                    if (bodypartsToWear.ContainsKey(bodypartItem.WearableType))
                    {
                        continue;
                    }

                    bodypartsToWear[bodypartItem.WearableType] = bodypartItem;
                    continue;
                }
                else if (inventoryItem.AssetType == AssetType.Gesture)
                {
                    gesturesToActivate[inventoryItem.UUID] = inventoryItem;
                }
                else if (inventoryItem.AssetType == AssetType.Clothing)
                {
                    if (numClothingLayers >= MaxClothingLayers)
                    {
                        continue;
                    }

                    numClothingLayers++;
                }
                else if (inventoryItem.AssetType == AssetType.Object)
                {
                    if (numAttachedObjects >= Client.Self.Benefits.AttachmentLimit)
                    {
                        continue;
                    }

                    ++numAttachedObjects;
                }

                itemsToWear[inventoryItem.UUID] = inventoryItem;
            }

            var existingLinkTargets = currentOutfitContents
                .OfType<InventoryItem>()
                .Where(n => !n.IsLink())
                .ToDictionary(k => k.UUID, v => v);
            var linksToRemove = new List<InventoryBase>();
            var gesturesToDeactivate = new HashSet<UUID>();

            foreach (var item in currentOutfitContents)
            {
                if (!(item is InventoryItem itemLink))
                {
                    continue;
                }

                if (!itemLink.IsLink())
                {
                    continue;
                }

                if (!existingLinkTargets.TryGetValue(itemLink.AssetUUID, out var linkTarget))
                {
                    linksToRemove.Add(itemLink);
                    continue;
                }

                if (linkTarget.AssetType == AssetType.Bodypart)
                {
                    existingBodypartLinks.Add(itemLink);
                    continue;
                }

                if (linkTarget.AssetType == AssetType.Gesture)
                {
                    if (!gesturesToActivate.ContainsKey(linkTarget.UUID))
                    {
                        gesturesToDeactivate.Add(linkTarget.UUID);
                    }
                }

                linksToRemove.Add(itemLink);
            }

            // Deactivate old gestures, activate new gestures
            foreach (var gestureId in gesturesToDeactivate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Client.Self.DeactivateGesture(gestureId);
            }
            foreach (var item in gesturesToActivate.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Client.Self.ActivateGesture(item.UUID, item.AssetUUID);
            }

            // Replace bodyparts, but keep old bodyparts if new outfit lacks them
            foreach (var existingLink in existingBodypartLinks)
            {
                if (existingLinkTargets.TryGetValue(existingLink.AssetUUID, out var realItem))
                {
                    if (realItem is InventoryWearable existingBodypart)
                    {
                        if (!bodypartsToWear.ContainsKey(existingBodypart.WearableType))
                        {
                            bodypartsToWear[existingBodypart.WearableType] = existingBodypart;
                            continue;
                        }
                    }
                }

                linksToRemove.Add(existingLink);
            }

            // Bare minimum outfit check
            if (!bodypartsToWear.ContainsKey(WearableType.Shape) ||
                !bodypartsToWear.ContainsKey(WearableType.Skin) ||
                !bodypartsToWear.ContainsKey(WearableType.Eyes) ||
                !bodypartsToWear.ContainsKey(WearableType.Hair))
            {
                Logger.Log("New outfit must contain a Shape, Skin, Eyes, and Hair", Helpers.LogLevel.Error, Client);
                return false;
            }

            // Clear out all existing current outfit links
            var toRemoveIds = linksToRemove
                .Select(n => n.UUID)
                .Distinct();
            await Client.Inventory.RemoveItemsAsync(toRemoveIds, cancellationToken);

            // Add new outfit links
            foreach (var item in bodypartsToWear)
            {
                await AddLink(item.Value, cancellationToken);
            }
            foreach (var item in itemsToWear)
            {
                await AddLink(item.Value, cancellationToken);
            }

            // Add link to outfit folder we're putting on
            if (newOutfitFolderNode != null)
            {
                Client.Inventory.CreateLink(
                    currentOutfitFolder.UUID,
                    newOutfitFolderNode.Data.UUID,
                    newOutfitFolderNode.Data.Name,
                    "",
                    InventoryType.Folder,
                    UUID.Random(),
                    (success, newItem) =>
                    {
                        if (success)
                        {
                            Client.Inventory.RequestFetchInventory(newItem.UUID, newItem.OwnerID);
                        }
                    },
                    cancellationToken
                );
            }

            // Wear new outfit
            var tcs = new TaskCompletionSource<bool>();
            void handleAppearanceSet(object sender, AppearanceSetEventArgs e)
            {
                tcs.TrySetResult(true);
            }

            try
            {
                Client.Appearance.AppearanceSet += handleAppearanceSet;
                Client.Appearance.ReplaceOutfit(itemsToWear.Values.ToList(), false);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
                if (completedTask != tcs.Task)
                {
                    Logger.Log("Timed out while waiting for AppearanceSet confirmation. Are you changing outfits too quickly?", Helpers.LogLevel.Error, Client);
                    return false;
                }
            }
            finally
            {
                Client.Appearance.AppearanceSet -= handleAppearanceSet;
            }

            return true;
        }

        /// <summary>
        /// Add items to current outfit
        /// </summary>
        /// <param name="item">Item to add</param>
        /// <param name="replace">Should existing wearable of the same type be removed</param>
        public async Task AddToOutfit(InventoryItem item, bool replace, CancellationToken cancellationToken = default)
        {
            await AddToOutfit(new List<InventoryItem>(1) { item }, replace, cancellationToken);
        }

        /// <summary>
        /// Add items to current outfit
        /// </summary>
        /// <param name="itemsToAdd">List of items to add</param>
        /// <param name="replace">Should existing wearable of the same type be removed</param>
        public async Task AddToOutfit(List<InventoryItem> itemsToAdd, bool replace, CancellationToken cancellationToken = default)
        {
            // TODO: Copy from library if necessary

            if (COF == null)
            {
                Logger.Log("Can't add to outfit link; COF hasn't been initialized.", Helpers.LogLevel.Warning, Client);
                return;
            }

            var trashFolderId = Client.Inventory.FindFolderForType(FolderType.Trash);
            var rootFolderId = Client.Inventory.Store.RootFolder.UUID;

            var cofLinks = await GetCurrentOutfitLinks(cancellationToken);
            var cofRealItems = new Dictionary<UUID, InventoryBase>();
            var cofLinkAssetIds = new HashSet<UUID>();
            var currentBodyparts = new Dictionary<WearableType, InventoryWearable>();
            var currentClothing = new Dictionary<WearableType, List<InventoryWearable>>();
            var currentAttachmentPoints = new Dictionary<AttachmentPoint, List<InventoryObject>>();
            var numClothingLayers = 0;
            var numAttachedObjects = 0;

            foreach (var item in cofLinks)
            {
                var realItem = Instance.COF.ResolveInventoryLink(item) ?? item;
                if (realItem == null)
                {
                    continue;
                }

                cofRealItems[realItem.UUID] = realItem;
                cofLinkAssetIds.Add(item.AssetUUID);

                if (realItem is InventoryWearable wearable)
                {
                    if (realItem.AssetType == AssetType.Bodypart)
                    {
                        currentBodyparts[wearable.WearableType] = wearable;
                    }
                    else if (realItem.AssetType == AssetType.Clothing)
                    {
                        if (!currentClothing.TryGetValue(wearable.WearableType, out var currentWearablesOfType))
                        {
                            currentWearablesOfType = new List<InventoryWearable>();
                            currentClothing[wearable.WearableType] = currentWearablesOfType;
                            numClothingLayers++;
                        }

                        currentWearablesOfType.Add(wearable);
                    }
                }
                else if (realItem is InventoryObject inventoryObject)
                {
                    if (!currentAttachmentPoints.TryGetValue(inventoryObject.AttachPoint, out var attachedObjects))
                    {
                        attachedObjects = new List<InventoryObject>();
                        currentAttachmentPoints[inventoryObject.AttachPoint] = attachedObjects;
                    }

                    attachedObjects.Add(inventoryObject);
                    numAttachedObjects++;
                }
            }

            var linksToRemove = new List<UUID>();

            // Resolve inventory links and remove wearables of the same type from COF
            var outfit = new List<InventoryItem>();

            foreach (var item in itemsToAdd)
            {
                var realItem = Instance.COF.ResolveInventoryLink(item);
                if (realItem == null)
                {
                    continue;
                }

                var isItemInTrash = await Instance.COF.IsObjectDescendentOf(realItem, trashFolderId, cancellationToken);
                if (isItemInTrash)
                {
                    continue;
                }

                var isItemInInventory = await Instance.COF.IsObjectDescendentOf(realItem, rootFolderId, cancellationToken);
                if (!isItemInInventory)
                {
                    continue;
                }

                if (cofLinkAssetIds.Contains(realItem.UUID))
                {
                    continue;
                }
                if (outfit.FirstOrDefault(n => n.UUID == realItem.UUID) != null)
                {
                    continue;
                }

                if (realItem is InventoryWearable wearable)
                {
                    if (wearable.AssetType == AssetType.Clothing)
                    {
                        if (replace)
                        {
                            if (currentClothing.TryGetValue(wearable.WearableType, out var currentClothingOfType))
                            {
                                // Remove all existing clothing links for this wearable type
                                foreach (var clothingToRemove in currentClothingOfType)
                                {
                                    var clothingLinksToRemove = cofLinks
                                        .Where(n => n.IsLink() && n.AssetUUID == clothingToRemove.UUID)
                                        .Select(n => n.UUID);
                                    linksToRemove.AddRange(clothingLinksToRemove);
                                }
                            }
                        }
                        else
                        {
                            if (numClothingLayers >= MaxClothingLayers)
                            {
                                continue;
                            }

                            numClothingLayers++;
                        }
                    }
                    else if (wearable.AssetType == AssetType.Bodypart)
                    {
                        if (currentBodyparts.TryGetValue(wearable.WearableType, out var existingBodyPart))
                        {
                            var bodypartLinksToRemove = cofLinks
                                .Where(n => n.IsLink() && n.AssetUUID == existingBodyPart.UUID)
                                .Select(n => n.UUID);
                            linksToRemove.AddRange(bodypartLinksToRemove);
                        }
                    }
                }
                else if (realItem.AssetType == AssetType.Gesture)
                {
                    Client.Self.ActivateGesture(realItem.UUID, realItem.AssetUUID);
                }
                else if (realItem is InventoryObject objectToAdd)
                {
                    if (replace)
                    {
                        // TODO: It's really confusing what should be done with AddToOutfit(replace=true) with objects
                        if (currentAttachmentPoints.TryGetValue(objectToAdd.AttachPoint, out var attachedObjectsToRemove))
                        {
                            foreach (var attachedObject in attachedObjectsToRemove)
                            {
                                var attachedObjectLinksToRemove = cofLinks
                                    .Where(n => n.IsLink() && n.AssetUUID == attachedObject.UUID)
                                    .Select(n => n.UUID);
                                linksToRemove.AddRange(attachedObjectLinksToRemove);
                            }
                        }
                    }
                    else
                    {
                        if (numAttachedObjects >= Client.Self.Benefits.AttachmentLimit)
                        {
                            continue;
                        }

                        ++numAttachedObjects;
                    }
                }
                else
                {
                    continue;
                }

                outfit.Add(realItem);
            }

            if (linksToRemove.Count > 0)
            {
                await Client.Inventory.RemoveItemsAsync(linksToRemove, cancellationToken);
            }

            // Add links to new items
            foreach (var item in outfit)
            {
                await AddLink(item, cancellationToken);
            }

            Client.Appearance.AddToOutfit(outfit, replace);
            ThreadPool.QueueUserWorkItem(sync =>
            {
                Thread.Sleep(2000);
                Client.Appearance.RequestSetAppearance(true);
            });
        }

        /// <summary>
        /// Removes specified item from the current outfit. All COF links to this item will be removed from the COF.
        /// The specified item may either be an actual item, or a link to an actual item. Links will be resolved to the
        /// actual item internally.
        /// </summary>
        /// <param name="item">Item (or item link) we want to remove all links to from our COF</param>
        /// <param name="cancellationToken"></param>
        public async Task RemoveFromOutfit(InventoryItem item, CancellationToken cancellationToken = default)
        {
            await RemoveFromOutfit(new List<InventoryItem>(1) { item }, cancellationToken);
        }

        /// <summary>
        /// Removes specified items from the current outfit. All COF links to these items will be removed from the COF.
        /// The specified items may either be actual items, or links to actual items. Links will be resolved to actual
        /// items internally.
        /// </summary>
        /// <param name="itemsToRemoveFromOutfit">List of items (or item links) we want to remove all links to from our COF</param>
        /// <param name="cancellationToken"></param>
        public async Task RemoveFromOutfit(List<InventoryItem> itemsToRemoveFromOutfit, CancellationToken cancellationToken = default)
        {
            if (COF == null)
            {
                Logger.Log("Can't remove from outfit; COF hasn't been initialized.", Helpers.LogLevel.Warning, Client);
                return;
            }

            var itemsToRemove = itemsToRemoveFromOutfit
                .Select(n => Instance.COF.ResolveInventoryLink(n))
                .Where(n => n != null && !IsBodyPart(n))
                .Distinct()
                .ToList();
            foreach (var item in itemsToRemove)
            {
                if (item.AssetType == AssetType.Gesture)
                {
                    Client.Self.DeactivateGesture(item.UUID);
                }
            }

            var itemIdsToRemove = itemsToRemove
                .Select(n => n.ActualUUID)
                .Distinct()
                .ToList();

            await RemoveLinks(itemIdsToRemove, cancellationToken);
            Client.Appearance.RemoveFromOutfit(itemsToRemove);
        }

        #endregion Public methods

        #region UnrelatedToCOF

        /// <summary>
        /// Get the inventory ID of an attached prim
        /// </summary>
        /// <param name="prim">Prim to check</param>
        /// <returns>Inventory ID of the object. UUID.Zero if not found</returns>
        public static UUID GetAttachmentItemID(Primitive prim)
        {
            if (prim.NameValues == null)
            {
                return UUID.Zero;
            }

            var attachmentId = prim.NameValues
                .Where(n => n.Name == "AttachItemID")
                .Select(n => new UUID(n.Value.ToString()))
                .FirstOrDefault();

            return attachmentId;
        }

        /// <summary>
        /// Retrieves the linked item from <paramref name="itemLink"/> if it is a link.
        /// </summary>
        /// <param name="itemLink">The link to an inventory item</param>
        /// <returns>
        /// The original inventory item, or null if the link could not be resolved
        /// </returns>
        public InventoryItem ResolveInventoryLink(InventoryItem itemLink)
        {
            if (itemLink.AssetType != AssetType.Link)
            {
                return itemLink;
            }

            if (!Client.Inventory.Store.TryGetValue<InventoryItem>(itemLink.AssetUUID, out var inventoryItem))
            {
                Client.Inventory.RequestFetchInventory(itemLink.AssetUUID, itemLink.OwnerID);

                if (!Client.Inventory.Store.TryGetValue<InventoryItem>(itemLink.AssetUUID, out inventoryItem))
                {
                    return null;
                }
            }

            return inventoryItem;
        }

        /// <summary>
        /// Retrieves the parent of <paramref name="item"/>
        /// </summary>
        /// <param name="item">Item to retrieve the parent of</param>
        /// <param name="cancellationToken"></param>
        /// <returns>The parent of <paramref name="item"/>, or null if item has no parent or parent does not exist</returns>
        public async Task<InventoryBase> FetchParent(InventoryBase item, CancellationToken cancellationToken = default)
        {
            if (item.ParentUUID == UUID.Zero)
            {
                return null;
            }

            if (!Client.Inventory.Store.TryGetNodeFor(item.ParentUUID, out var parent))
            {
                var fetchedParent = await Client.Inventory.FetchItemHttpAsync(item.ParentUUID, item.OwnerID, cancellationToken);
                return fetchedParent;
            }

            return parent.Data;
        }

        /// <summary>
        /// Determines if inventoy item <paramref name="item"/> is a descendant of inventory folder <paramref name="parentId"/>
        /// </summary>
        /// <param name="item">Item to check</param>
        /// <param name="parentId">ID of the folder to check</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True if <paramref name="item"/> exists as a child, or sub-child of folder <paramref name="parentId"/></returns>
        public async Task<bool> IsObjectDescendentOf(InventoryBase item, UUID parentId, CancellationToken cancellationToken = default)
        {
            const int kArbritrayDepthLimit = 255;

            if (parentId == UUID.Zero)
            {
                return false;
            }

            var parentIter = item;
            for (var i = 0; i < kArbritrayDepthLimit; ++i)
            {
                if (parentIter.ParentUUID == parentId)
                {
                    return true;
                }

                parentIter = await FetchParent(parentIter, cancellationToken);
                if (parentIter == null)
                {
                    return false;
                }
            }

            return false;
        }
        #endregion
    }
}
