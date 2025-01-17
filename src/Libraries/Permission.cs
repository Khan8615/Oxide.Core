extern alias References;

using Oxide.Core.Plugins;
using References::ProtoBuf;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;

namespace Oxide.Core.Libraries
{
    /// <summary>
    /// Contains all data for a specified user
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
    public class UserData
    {
        /// <summary>
        /// Gets or sets the last seen nickname for this player
        /// </summary>
        public string LastSeenNickname { get; set; } = "Unnamed";

        /// <summary>
        /// Gets or sets the individual permissions for this player
        /// </summary>
        public HashSet<string> Perms { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the group for this player
        /// </summary>
        public HashSet<string> Groups { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Contains all data for a specified group
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
    public class GroupData
    {
        /// <summary>
        /// Gets or sets the title of this group
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the rank of this group
        /// </summary>
        public int Rank { get; set; }

        /// <summary>
        /// Gets or sets the individual permissions for this group
        /// </summary>
        public HashSet<string> Perms { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the parent for this group
        /// </summary>
        public string ParentGroup { get; set; } = string.Empty;
    }

    /// <summary>
    /// A library providing a unified permissions system
    /// </summary>
    public class Permission : Library
    {
        /// <summary>
        /// Returns if this library should be loaded into the global namespace
        /// </summary>
        public override bool IsGlobal => false;

        // All registered permissions
        private readonly Dictionary<Plugin, HashSet<string>> permset;

        // All users data
        private Dictionary<string, UserData> userdata;

        // All groups data
        private Dictionary<string, GroupData> groupdata;

        private Func<string, bool> validate;

        // Permission status
        public bool IsLoaded { get; private set; }

        /// <summary>
        /// Initializes a new instance of the Permission library
        /// </summary>
        public Permission()
        {
            // Initialize
            permset = new Dictionary<Plugin, HashSet<string>>();

            // Load the datafile
            LoadFromDatafile();
        }

        /// <summary>
        /// Loads all permissions data from the datafile
        /// </summary>
        private void LoadFromDatafile()
        {
            Utility.DatafileToProto<Dictionary<string, UserData>>("oxide.users");
            Utility.DatafileToProto<Dictionary<string, GroupData>>("oxide.groups");
            userdata = ProtoStorage.Load<Dictionary<string, UserData>>("oxide.users")?.ToDictionary(x => x.Key, x => x.Value,
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, UserData>(StringComparer.OrdinalIgnoreCase);
            groupdata = ProtoStorage.Load<Dictionary<string, GroupData>>("oxide.groups")?.ToDictionary(x => x.Key, x => x.Value,
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, GroupData>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, GroupData> pair in groupdata)
            {
                if (string.IsNullOrEmpty(pair.Value.ParentGroup) || !HasCircularParent(pair.Key, pair.Value.ParentGroup))
                {
                    continue;
                }

                Interface.Oxide.LogWarning("Detected circular parent group for '{0}'; removing parent '{1}'", pair.Key, pair.Value.ParentGroup);
                pair.Value.ParentGroup = null;
            }

            IsLoaded = true;
        }

        /// <summary>
        /// Exports user/group data to json
        /// </summary>
        [LibraryFunction("Export")]
        public void Export(string prefix = "auth")
        {
            if (IsLoaded)
            {
                Interface.Oxide.DataFileSystem.WriteObject(prefix + ".groups", groupdata);
                Interface.Oxide.DataFileSystem.WriteObject(prefix + ".users", userdata);
            }
        }

        /// <summary>
        /// Saves all permissions data to the data files
        /// </summary>
        public void SaveData()
        {
            SaveUsers();
            SaveGroups();
        }

        /// <summary>
        /// Saves users permissions data to the data file
        /// </summary>
        public void SaveUsers() => ProtoStorage.Save(userdata, "oxide.users");

        /// <summary>
        /// Saves groups permissions data to the data file
        /// </summary>
        public void SaveGroups() => ProtoStorage.Save(groupdata, "oxide.groups");

        /// <summary>
        /// Register user ID validation
        /// </summary>
        /// <param name="val"></param>
        public void RegisterValidate(Func<string, bool> val) => validate = val;

        /// <summary>
        /// Clean invalid user ID entries
        /// </summary>
        public void CleanUp()
        {
            if (IsLoaded && validate != null)
            {
                string[] invalidData = userdata.Keys.Where(i => !validate(i)).ToArray();
                if (invalidData.Length <= 0)
                {
                    return;
                }

                foreach (string i in invalidData)
                {
                    userdata.Remove(i);
                }
            }
        }

        /// <summary>
        /// Migrate permissions from one group to another
        /// </summary>
        public void MigrateGroup(string oldGroupName, string newGroupName)
        {
            if (IsLoaded && GroupExists(oldGroupName))
            {
                string groups = ProtoStorage.GetFileDataPath("oxide.groups.data");
                File.Copy(groups, groups + ".old", true);

                foreach (string permission in GetGroupPermissions(oldGroupName))
                {
                    GrantGroupPermission(newGroupName, permission, null);
                }

                if (GetUsersInGroup(oldGroupName).Length == 0)
                {
                    RemoveGroup(oldGroupName);
                }
            }
        }

        #region Permission Management

        /// <summary>
        /// Registers the specified permission
        /// </summary>
        /// <param name="permission"></param>
        /// <param name="owner"></param>
        [LibraryFunction("RegisterPermission")]
        public void RegisterPermission(string permission, Plugin owner)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return;
            }

            if (PermissionExists(permission))
            {
                Interface.Oxide.LogWarning("Duplicate permission registered '{0}' (by plugin '{1}')", permission, owner.Title);
                return;
            }

            if (!permset.TryGetValue(owner, out HashSet<string> set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                permset.Add(owner, set);
                owner.OnRemovedFromManager.Add(owner_OnRemovedFromManager);
            }
            set.Add(permission);

            Interface.CallHook("OnPermissionRegistered", permission, owner);

            if (!permission.StartsWith($"{owner.Name}.", StringComparison.OrdinalIgnoreCase) && !owner.IsCorePlugin)
            {
                Interface.Oxide.LogWarning("Missing plugin name prefix '{0}' for permission '{1}' (by plugin '{2}')", owner.Name, permission, owner.Title);
            }
        }

        /// <summary>
        /// Returns if the specified permission exists or not
        /// </summary>
        /// <param name="permission"></param>
        /// <param name="owner"></param>
        /// <returns></returns>
        [LibraryFunction("PermissionExists")]
        public bool PermissionExists(string permission, Plugin owner = null)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return false;
            }

            if (owner == null)
            {
                if (permset.Count > 0)
                {
                    if (permission.Equals("*"))
                    {
                        return true;
                    }

                    if (permission.EndsWith("*"))
                    {
                        return permset.Values.SelectMany(v => v).Any(p => p.StartsWith(permission.TrimEnd('*'), StringComparison.OrdinalIgnoreCase));
                    }
                }
                return permset.Values.Any(v => v.Contains(permission));
            }

            if (!permset.TryGetValue(owner, out HashSet<string> set))
            {
                return false;
            }

            if (set.Count > 0)
            {
                if (permission.Equals("*"))
                {
                    return true;
                }

                if (permission.EndsWith("*"))
                {
                    return set.Any(p => p.StartsWith(permission.TrimEnd('*'), StringComparison.OrdinalIgnoreCase));
                }
            }
            return set.Contains(permission);
        }

        #endregion Permission Management

        /// <summary>
        /// Called when a plugin has been unloaded
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="manager"></param>
        private void owner_OnRemovedFromManager(Plugin sender, PluginManager manager) => permset.Remove(sender);

        #region Querying

        /// <summary>
        /// Returns if the specified user id is valid
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        [LibraryFunction("UserIdValid")]
        public bool UserIdValid(string playerId) => validate == null || validate(playerId);

        /// <summary>
        /// Returns if the specified user exists
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        [LibraryFunction("UserExists")]
        public bool UserExists(string playerId) => userdata.ContainsKey(playerId);

        /// <summary>
        /// Returns the data for the specified user
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        public UserData GetUserData(string playerId)
        {
            if (!userdata.TryGetValue(playerId, out UserData userData))
            {
                userdata.Add(playerId, userData = new UserData());
            }

            // Return the data
            return userData;
        }

        /// <summary>
        /// Updates the nickname
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="playerName"></param>
        [LibraryFunction("UpdateNickname")]
        public void UpdateNickname(string playerId, string playerName)
        {
            if (UserExists(playerId))
            {
                UserData userData = GetUserData(playerId);
                string oldName = userData.LastSeenNickname;
                string newName = playerName.Sanitize();
                userData.LastSeenNickname = playerName.Sanitize();

                Interface.CallHook("OnUserNameUpdated", playerId, oldName, newName);
            }
        }

        /// <summary>
        /// Check if user has a group
        /// </summary>
        /// <param name="playerId"></param>
        [LibraryFunction("UserHasAnyGroup")]
        public bool UserHasAnyGroup(string playerId) => UserExists(playerId) && GetUserData(playerId).Groups.Count > 0;

        /// <summary>
        /// Returns if the specified group has the specified permission or not
        /// </summary>
        /// <param name="groupNames"></param>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("GroupsHavePermission")]
        public bool GroupsHavePermission(HashSet<string> groupNames, string permission) => groupNames.Any(g => GroupHasPermission(g, permission));

        /// <summary>
        /// Returns if the specified group has the specified permission or not
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("GroupHasPermission")]
        public bool GroupHasPermission(string groupName, string permission)
        {
            if (!GroupExists(groupName) || string.IsNullOrEmpty(permission))
            {
                return false;
            }

            // Check if the group has the permission
            if (!groupdata.TryGetValue(groupName, out GroupData groupData))
            {
                return false;
            }

            return groupData.Perms.Contains(permission) || GroupHasPermission(groupData.ParentGroup, permission);
        }

        /// <summary>
        /// Returns if the specified user has the specified permission
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("UserHasPermission")]
        public bool UserHasPermission(string playerId, string permission)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return false;
            }

            // Always allow the server console
            if (playerId.Equals("server_console"))
            {
                return true;
            }

            // First, get the player data
            UserData userData = GetUserData(playerId);

            // Check if they have the permission
            if (userData.Perms.Contains(permission))
            {
                return true;
            }

            // Check if their group has the permission
            return GroupsHavePermission(userData.Groups, permission);
        }

        /// <summary>
        /// Returns the group to which the specified user belongs
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        [LibraryFunction("GetUserGroups")]
        public string[] GetUserGroups(string playerId) => GetUserData(playerId).Groups.ToArray();

        /// <summary>
        /// Returns the permissions which the specified user has
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        [LibraryFunction("GetUserPermissions")]
        public string[] GetUserPermissions(string playerId)
        {
            UserData userData = GetUserData(playerId);
            List<string> permissions = userData.Perms.ToList();
            foreach (string groupName in userData.Groups)
            {
                permissions.AddRange(GetGroupPermissions(groupName));
            }

            return new HashSet<string>(permissions).ToArray();
        }

        /// <summary>
        /// Returns the permissions which the specified group has, with optional transversing of parent groups
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="parents"></param>
        /// <returns></returns>
        [LibraryFunction("GetGroupPermissions")]
        public string[] GetGroupPermissions(string groupName, bool parents = false)
        {
            if (!GroupExists(groupName))
            {
                return new string[0];
            }

            if (!groupdata.TryGetValue(groupName, out GroupData groupData))
            {
                return new string[0];
            }

            List<string> permissions = groupData.Perms.ToList();

            if (parents)
            {
                permissions.AddRange(GetGroupPermissions(groupData.ParentGroup));
            }

            return new HashSet<string>(permissions).ToArray();
        }

        /// <summary>
        /// Returns the permissions which are registered
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetPermissions")]
        public string[] GetPermissions() => new HashSet<string>(permset.Values.SelectMany(v => v)).ToArray();

        /// <summary>
        /// Returns the players with given permission
        /// </summary>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("GetPermissionUsers")]
        public string[] GetPermissionUsers(string permission)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return new string[0];
            }

            HashSet<string> permissionUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, UserData> data in userdata)
            {
                if (data.Value.Perms.Contains(permission))
                {
                    permissionUsers.Add($"{data.Key}({data.Value.LastSeenNickname})");
                }
            }

            return permissionUsers.ToArray();
        }

        /// <summary>
        /// Returns the groups with given permission
        /// </summary>
        /// <param name="permission"></param>
        /// <returns></returns>
        [LibraryFunction("GetPermissionGroups")]
        public string[] GetPermissionGroups(string permission)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return new string[0];
            }

            HashSet<string> permissionGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, GroupData> data in groupdata)
            {
                if (data.Value.Perms.Contains(permission))
                {
                    permissionGroups.Add(data.Key);
                }
            }

            return permissionGroups.ToArray();
        }

        /// <summary>
        /// Set the group to which the specified user belongs
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("AddUserGroup")]
        public void AddUserGroup(string playerId, string groupName)
        {
            if (!GroupExists(groupName))
            {
                return;
            }

            UserData userData = GetUserData(playerId);

            if (!userData.Groups.Add(groupName))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnUserGroupAdded", playerId, groupName);
        }

        /// <summary>
        /// Set the group to which the specified user belongs
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("RemoveUserGroup")]
        public void RemoveUserGroup(string playerId, string groupName)
        {
            if (!GroupExists(groupName))
            {
                return;
            }

            UserData userData = GetUserData(playerId);

            if (groupName.Equals("*"))
            {
                if (userData.Groups.Count <= 0)
                {
                    return;
                }

                userData.Groups.Clear();
                return;
            }

            if (!userData.Groups.Remove(groupName))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnUserGroupRemoved", playerId, groupName);
        }

        /// <summary>
        /// Get if the player belongs to given group
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("UserHasGroup")]
        public bool UserHasGroup(string playerId, string groupName)
        {
            if (!GroupExists(groupName))
            {
                return false;
            }

            return GetUserData(playerId).Groups.Contains(groupName);
        }

        /// <summary>
        /// Returns if the specified group exists or not
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("GroupExists")]
        public bool GroupExists(string groupName)
        {
            return !string.IsNullOrEmpty(groupName) && (groupName.Equals("*") || groupdata.ContainsKey(groupName));
        }

        /// <summary>
        /// Returns existing groups
        /// </summary>
        /// <returns></returns>
        [LibraryFunction("GetGroups")]
        public string[] GetGroups() => groupdata.Keys.ToArray();

        /// <summary>
        /// Returns users in that group
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("GetUsersInGroup")]
        public string[] GetUsersInGroup(string groupName)
        {
            if (!GroupExists(groupName))
            {
                return new string[0];
            }

            return userdata.Where(u => u.Value.Groups.Contains(groupName)).Select(u => $"{u.Key} ({u.Value.LastSeenNickname})").ToArray();
        }

        /// <summary>
        /// Returns the title of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        [LibraryFunction("GetGroupTitle")]
        public string GetGroupTitle(string groupName)
        {
            if (!GroupExists(groupName))
            {
                return string.Empty;
            }

            // First, get the group data
            if (!groupdata.TryGetValue(groupName, out GroupData groupData))
            {
                return string.Empty;
            }

            // Return the group title
            return groupData.Title;
        }

        /// <summary>
        /// Returns the rank of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <returns></returns>
        [LibraryFunction("GetGroupRank")]
        public int GetGroupRank(string groupName)
        {
            if (!GroupExists(groupName))
            {
                return 0;
            }

            // First, get the group data
            if (!groupdata.TryGetValue(groupName, out GroupData groupData))
            {
                return 0;
            }

            // Return the group rank
            return groupData.Rank;
        }

        #endregion Querying

        #region User Permission

        /// <summary>
        /// Grants the specified permission to the specified user
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="perm"></param>
        /// <param name="owner"></param>
        [LibraryFunction("GrantUserPermission")]
        public void GrantUserPermission(string playerId, string perm, Plugin owner)
        {
            // Check it is even a perm
            if (!PermissionExists(perm, owner))
            {
                return;
            }

            // Get the player data
            UserData userData = GetUserData(playerId);

            if (perm.EndsWith("*"))
            {
                HashSet<string> permissions;

                if (owner == null)
                {
                    permissions = new HashSet<string>(permset.Values.SelectMany(v => v));
                }
                else if (!permset.TryGetValue(owner, out permissions))
                {
                    return;
                }

                if (perm.Equals("*"))
                {
                    if (!permissions.Aggregate(false, (c, s) => c | userData.Perms.Add(s)))
                    {
                        return;
                    }
                }
                else
                {
                    if (!permissions.Where(p => p.StartsWith(perm.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)).Aggregate(false, (c, s) => c | userData.Perms.Add(s)))
                    {
                        return;
                    }
                }
                return;
            }

            // Add the permission
            if (!userData.Perms.Add(perm))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnUserPermissionGranted", playerId, perm);
        }

        /// <summary>
        /// Revokes the specified permission from the specified user
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="permission"></param>
        [LibraryFunction("RevokeUserPermission")]
        public void RevokeUserPermission(string playerId, string permission)
        {
            if (string.IsNullOrEmpty(permission))
            {
                return;
            }

            // Get the player data
            UserData userData = GetUserData(playerId);

            if (permission.EndsWith("*"))
            {
                if (permission.Equals("*"))
                {
                    if (userData.Perms.Count <= 0)
                    {
                        return;
                    }

                    userData.Perms.Clear();
                }
                else
                {
                    if (userData.Perms.RemoveWhere(p => p.StartsWith(permission.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)) <= 0)
                    {
                        return;
                    }
                }
                return;
            }

            // Remove the permission
            if (!userData.Perms.Remove(permission))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnUserPermissionRevoked", playerId, permission);
        }

        #endregion User Permission

        #region Group Permission

        /// <summary>
        /// Grant the specified permission to the specified group
        /// </summary>
        /// <param name="name"></param>
        /// <param name="perm"></param>
        /// <param name="owner"></param>
        [LibraryFunction("GrantGroupPermission")]
        public void GrantGroupPermission(string name, string perm, Plugin owner)
        {
            // Check it is even a perm
            if (!PermissionExists(perm, owner) || !GroupExists(name))
            {
                return;
            }

            // Get the group data
            if (!groupdata.TryGetValue(name, out GroupData groupData))
            {
                return;
            }

            if (perm.EndsWith("*"))
            {
                HashSet<string> permissions;

                if (owner == null)
                {
                    permissions = new HashSet<string>(permset.Values.SelectMany(v => v));
                }
                else if (!permset.TryGetValue(owner, out permissions))
                {
                    return;
                }

                if (perm.Equals("*"))
                {
                    if (!permissions.Aggregate(false, (c, s) => c | groupData.Perms.Add(s)))
                    {
                        return;
                    }
                }
                else
                {
                    if (!permissions.Where(p => p.StartsWith(perm.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)).Aggregate(false, (c, s) => c | groupData.Perms.Add(s)))
                    {
                        return;
                    }
                }

                return;
            }

            // Add the permission
            if (!groupData.Perms.Add(perm))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnGroupPermissionGranted", name, perm);
        }

        /// <summary>
        /// Revokes the specified permission from the specified user
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="permission"></param>
        [LibraryFunction("RevokeGroupPermission")]
        public void RevokeGroupPermission(string groupName, string permission)
        {
            if (!GroupExists(groupName) || string.IsNullOrEmpty(permission))
            {
                return;
            }

            // Get the group data
            if (!groupdata.TryGetValue(groupName, out GroupData groupData))
            {
                return;
            }

            if (permission.EndsWith("*"))
            {
                if (permission.Equals("*"))
                {
                    if (groupData.Perms.Count <= 0)
                    {
                        return;
                    }

                    groupData.Perms.Clear();
                }
                else
                {
                    if (groupData.Perms.RemoveWhere(p => p.StartsWith(permission.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)) <= 0)
                    {
                        return;
                    }
                }
                return;
            }

            // Remove the permission
            if (!groupData.Perms.Remove(permission))
            {
                return;
            }

            // Call hook for plugins
            Interface.Call("OnGroupPermissionRevoked", groupName, permission);
        }

        #endregion Group Permission

        #region Group Management

        /// <summary>
        /// Creates the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="groupTitle"></param>
        /// <param name="groupRank"></param>
        [LibraryFunction("CreateGroup")]
        public bool CreateGroup(string groupName, string groupTitle, int groupRank)
        {
            // Check if it already exists
            if (GroupExists(groupName) || string.IsNullOrEmpty(groupName))
            {
                return false;
            }

            // Create the data
            GroupData groupData = new GroupData { Title = groupTitle, Rank = groupRank };

            // Add the group
            groupdata.Add(groupName, groupData);

            Interface.CallHook("OnGroupCreated", groupName, groupTitle, groupRank);

            return true;
        }

        /// <summary>
        /// Removes the specified group
        /// </summary>
        /// <param name="groupName"></param>
        [LibraryFunction("RemoveGroup")]
        public bool RemoveGroup(string groupName)
        {
            // Check if it even exists
            if (!GroupExists(groupName))
            {
                return false;
            }

            // Remove the group
            bool removed = groupdata.Remove(groupName);
            if (removed)
            {
                // Set children to having no parent group
                foreach (GroupData child in groupdata.Values)
                {
                    if (child.ParentGroup == groupName)
                        child.ParentGroup = string.Empty;
                }
            }

            // Remove group from users
            bool changed = userdata.Values.Aggregate(false, (current, userData) => current | userData.Groups.Remove(groupName));

            if (changed)
            {
                SaveUsers();
            }

            if (removed)
            {
                Interface.CallHook("OnGroupDeleted", groupName);
            }

            return true;
        }

        /// <summary>
        /// Sets the title of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="groupTitle"></param>
        [LibraryFunction("SetGroupTitle")]
        public bool SetGroupTitle(string groupName, string groupTitle)
        {
            if (!GroupExists(groupName))
            {
                return false;
            }

            // First, get the group data
            if (!groupdata.TryGetValue(groupName, out GroupData groupData))
            {
                return false;
            }

            // Change the title
            if (groupData.Title == groupTitle)
            {
                return true;
            }

            groupData.Title = groupTitle;

            Interface.CallHook("OnGroupTitleSet", groupName, groupTitle);

            return true;
        }

        /// <summary>
        /// Sets the rank of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="groupRank"></param>
        [LibraryFunction("SetGroupRank")]
        public bool SetGroupRank(string groupName, int groupRank)
        {
            if (!GroupExists(groupName))
            {
                return false;
            }

            // First, get the group data
            if (!groupdata.TryGetValue(groupName, out GroupData groupData))
            {
                return false;
            }

            // Change the rank
            if (groupData.Rank == groupRank)
            {
                return true;
            }

            groupData.Rank = groupRank;

            Interface.CallHook("OnGroupRankSet", groupName, groupRank);

            return true;
        }

        /// <summary>
        /// Gets the parent of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        [LibraryFunction("GetGroupParent")]
        public string GetGroupParent(string groupName)
        {
            if (!GroupExists(groupName))
            {
                return string.Empty;
            }

            return !groupdata.TryGetValue(groupName, out GroupData groupData) ? string.Empty : groupData.ParentGroup;
        }

        /// <summary>
        /// Sets the parent of the specified group
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="parentGroupName"></param>
        [LibraryFunction("SetGroupParent")]
        public bool SetGroupParent(string groupName, string parentGroupName)
        {
            if (!GroupExists(groupName))
            {
                return false;
            }

            // First, get the group data
            if (!groupdata.TryGetValue(groupName, out GroupData groupData))
            {
                return false;
            }

            if (string.IsNullOrEmpty(parentGroupName))
            {
                groupData.ParentGroup = null;
                return true;
            }

            if (!GroupExists(parentGroupName) || groupName.Equals(parentGroupName))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(groupData.ParentGroup) && groupData.ParentGroup.Equals(parentGroupName))
            {
                return true;
            }

            if (HasCircularParent(groupName, parentGroupName))
            {
                return false;
            }

            // Change the parent group
            groupData.ParentGroup = parentGroupName;

            Interface.CallHook("OnGroupParentSet", groupName, parentGroupName);

            return true;
        }

        private bool HasCircularParent(string groupName, string parentGroupName)
        {
            // Get parent data
            if (!groupdata.TryGetValue(parentGroupName, out GroupData parentGroupData))
            {
                return false;
            }

            HashSet<string> groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupName, parentGroupName };

            // Check for circular reference
            while (!string.IsNullOrEmpty(parentGroupData.ParentGroup))
            {
                // Found itself?
                if (!groupNames.Add(parentGroupData.ParentGroup))
                {
                    return true;
                }

                // Get next parent
                if (!groupdata.TryGetValue(parentGroupData.ParentGroup, out parentGroupData))
                {
                    return false;
                }
            }

            return false;
        }

        #endregion Group Management
    }
}
