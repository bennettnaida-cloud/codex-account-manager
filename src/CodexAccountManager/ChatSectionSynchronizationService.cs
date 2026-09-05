using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAccountManager;

internal readonly record struct ChatSectionSyncResult(
    int OAuthAccounts,
    int UpdatedProfiles,
    int AddedSections,
    int AddedItems,
    bool Changed,
    string? TemplateAccountId,
    string? BackupPath)
{
    public static ChatSectionSyncResult Empty(int accountCount = 0) =>
        new(accountCount, 0, 0, 0, false, null, null);
}

/// <summary>
/// Keeps Codex's local custom chat directories consistent between official OAuth
/// identities.  Codex persists the sidebar cache in one global JSON file, but
/// nests it below the ChatGPT account id.  The server-side thread sections remain
/// shared; this service only synchronizes the per-identity presentation cache.
/// </summary>
internal static class ChatSectionSynchronizationService
{
    private const string GlobalStateFileName = ".codex-global-state.json";
    private const string PersistedStateName = "electron-persisted-atom-state";
    private const string ProfilesStateName = "sidebar-custom-sections-v3";
    // Codex uses this literal profile key while auth_mode=apikey. PAT and compatible
    // API launches therefore never select their whoami GUID bucket in the renderer.
    private const string LocalProfileAccountId = "__local__";
    private const string SectionsStateName = "sections";
    private const string SectionOrderStateName = "sectionOrder";
    private const string CollapsedSectionIdsStateName = "collapsedSectionIds";
    private const string ItemKeysStateName = "itemKeys";
    private const string HostSectionIdsStateName = "hostSectionIds";
    private const string LocalHostName = "local";
    private const string LocalThreadKeyPrefix = "codex:thread:local:";
    private const string SyncMutexName = "Local\\CodexAccountManager.ChatSectionSync";
    private static readonly Mutex SyncMutex = new(false, SyncMutexName);
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    internal static ChatSectionSyncResult Synchronize(
        IReadOnlyList<AccountRecord> accounts,
        string codexHome,
        string? preferredAccountName = null,
        string? managerRoot = null)
    {
        var identityStore = new PatGatewayAccountIdentityStore(
            managerRoot ?? new AccountStore().RootPath);
        var profileAccounts = accounts
            .Where(account => account.IsOfficialOAuth)
            .Select(account => TryReadIdentity(account, out var identity)
                ? identity
                : null)
            .Where(identity => identity != null)
            .Select(identity => identity!)
            .GroupBy(identity => identity.AccountId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        // PAT accounts do not own an auth.json ChatGPT identity, but a successful
        // whoami response gives us the same stable account id. Merge those cached
        // identities into the exact same sidebar buckets without persisting the PAT.
        var cachedPatIdentities = identityStore.Load();
        foreach (var account in accounts.Where(candidate =>
                     candidate.IsAccessToken && !candidate.IsCompatibleApi))
        {
            var accountKey = QuotaAccountIdentity.CreateKey(account);
            if (cachedPatIdentities.TryGetValue(accountKey, out var accountId) &&
                Guid.TryParse(accountId, out _))
            {
                profileAccounts.Add(new OAuthIdentity(accountId, account.Name));
            }
        }
        // The desktop renderer selects __local__ for every API-key authenticated
        // session. Keep that shared presentation bucket synchronized as well; otherwise
        // the GUID buckets are correct on disk while PAT/API users still see only Recent.
        var localAccount = accounts.FirstOrDefault(account =>
                               !account.IsOfficialOAuth &&
                               !string.IsNullOrWhiteSpace(preferredAccountName) &&
                               account.Name.Equals(
                                   preferredAccountName,
                                   StringComparison.OrdinalIgnoreCase)) ??
                           accounts.FirstOrDefault(account => !account.IsOfficialOAuth);
        if (localAccount != null)
        {
            profileAccounts.Add(new OAuthIdentity(LocalProfileAccountId, localAccount.Name));
        }

        profileAccounts = profileAccounts
            .GroupBy(identity => identity.AccountId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (profileAccounts.Count == 0)
        {
            return ChatSectionSyncResult.Empty();
        }

        var globalStatePath = Path.Combine(Path.GetFullPath(codexHome), GlobalStateFileName);
        if (!File.Exists(globalStatePath))
        {
            // A newly created profile has no sidebar state yet.  Once Codex writes
            // the file, the next LoadAccounts call will initialize the new bucket.
            return ChatSectionSyncResult.Empty(profileAccounts.Count);
        }

        var mutexAcquired = false;
        try
        {
            try
            {
                mutexAcquired = SyncMutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }

            if (!mutexAcquired)
            {
                return ChatSectionSyncResult.Empty(profileAccounts.Count);
            }

            var originalText = File.ReadAllText(globalStatePath, Encoding.UTF8);
            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(originalText) ? "{}" : originalText)
                       as JsonObject
                       ?? throw new InvalidOperationException("Codex 全局状态 JSON 格式无效。");
            var persisted = GetOrCreateObject(root, PersistedStateName);
            var profiles = GetOrCreateObject(persisted, ProfilesStateName);
            var profileByAccountId = ReadProfiles(profiles);
            var deletedThreadIds = SharedHistoryService.LoadDeletedThreadIds(codexHome);
            var officialSnapshot = LoadOfficialSectionSnapshot(codexHome);
            var availableThreadKeys = TryLoadLocalThreadKeys(codexHome, out var localThreadKeys)
                ? localThreadKeys
                : null;
            var template = SelectTemplate(profileAccounts, profileByAccountId, preferredAccountName);
            if ((template == null || template.Sections.Count == 0) &&
                (!officialSnapshot.Available || officialSnapshot.Sections.Count == 0))
            {
                return ChatSectionSyncResult.Empty(profileAccounts.Count);
            }

            var updatedProfiles = 0;
            var addedSections = 0;
            var addedItems = 0;
            foreach (var target in profileAccounts)
            {
                var targetProfile = GetOrCreateProfile(profiles, target.AccountId);
                var changed = false;
                if (template != null && template.Sections.Count > 0)
                {
                    changed |= MergeProfile(
                        targetProfile,
                        template,
                        deletedThreadIds,
                        availableThreadKeys,
                        ref addedSections,
                        ref addedItems);
                }
                if (officialSnapshot.Available)
                {
                    changed |= ReconcileOfficialProfile(
                        targetProfile,
                        officialSnapshot,
                        deletedThreadIds,
                        ref addedSections,
                        ref addedItems);
                }
                if (changed)
                {
                    updatedProfiles++;
                }
            }

            var projected = root.ToJsonString(CompactJson);
            if (string.Equals(originalText, projected, StringComparison.Ordinal))
            {
                return new ChatSectionSyncResult(
                    profileAccounts.Count,
                    0,
                    0,
                    0,
                    false,
                    template?.AccountId,
                    null);
            }

            var backupPath = CreateBackup(globalStatePath);
            WriteTextAtomically(globalStatePath, projected);
            return new ChatSectionSyncResult(
                profileAccounts.Count,
                updatedProfiles,
                addedSections,
                addedItems,
                true,
                template?.AccountId,
                backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Sidebar synchronization is best-effort.  A locked or partially-written
            // Codex state file must never stop account loading or gateway startup.
            ManagerLifecycleDiagnostics.WriteException(
                "chat-section-sync-failed",
                ex,
                $"accounts={profileAccounts.Count}");
            return ChatSectionSyncResult.Empty(profileAccounts.Count);
        }
        finally
        {
            if (mutexAcquired)
            {
                try
                {
                    SyncMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // The mutex can be abandoned while the manager is shutting down.
                }
            }
        }
    }

    private static bool MergeProfile(
        JsonObject targetProfile,
        TemplateProfile template,
        IReadOnlySet<string> deletedThreadIds,
        IReadOnlySet<string>? availableThreadKeys,
        ref int addedSections,
        ref int addedItems)
    {
        var targetSections = GetOrCreateArray(targetProfile, SectionsStateName);
        var targetByName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in targetSections.OfType<JsonObject>())
        {
            var name = ReadString(node, "name");
            if (!string.IsNullOrWhiteSpace(name) && !targetByName.ContainsKey(name))
            {
                EnsureSectionId(node);
                targetByName[name] = node;
            }
        }

        var targetIds = new HashSet<string>(
            targetByName.Values.Select(section => ReadString(section, "id")),
            StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var sourceSection in template.Sections)
        {
            var name = sourceSection.Name;
            if (!targetByName.TryGetValue(name, out var targetSection))
            {
                targetSection = sourceSection.Node.DeepClone()?.AsObject()
                                ?? new JsonObject();
                var id = NewSectionId(targetIds);
                targetSection["id"] = id;
                ReplaceItemKeys(targetSection, FilterItemKeys(
                    sourceSection.ItemKeys,
                    deletedThreadIds,
                    availableThreadKeys),
                    out var copiedItems);
                addedItems += copiedItems;
                targetSections.Add(targetSection);
                targetByName[name] = targetSection;
                targetIds.Add(id);
                addedSections++;
                changed = true;
            }
            else
            {
                EnsureSectionId(targetSection);
                var targetItems = GetOrCreateArray(targetSection, ItemKeysStateName);
                var before = targetItems.Count;
                var seen = new HashSet<string>(
                    targetItems.OfType<JsonValue>()
                        .Select(value => value.TryGetValue<string>(out var text) ? text : "")
                        .Where(text => !string.IsNullOrWhiteSpace(text)),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var itemKey in FilterItemKeys(
                             sourceSection.ItemKeys,
                             deletedThreadIds,
                             availableThreadKeys))
                {
                    if (seen.Add(itemKey))
                    {
                        targetItems.Add(itemKey);
                    }
                }
                var copiedItems = targetItems.Count - before;
                if (copiedItems > 0)
                {
                    addedItems += copiedItems;
                    changed = true;
                }
            }

        }

        changed |= RewriteSectionOrder(
            targetProfile,
            template,
            targetByName);
        changed |= RewriteCollapsedSections(
            targetProfile,
            template,
            targetByName);

        var migratedHosts = GetOrCreateArray(targetProfile, "appServerMigratedHostIds");
        if (!migratedHosts.OfType<JsonValue>().Any(value =>
                value.TryGetValue<string>(out var host) &&
                host.Equals(LocalHostName, StringComparison.OrdinalIgnoreCase)))
        {
            migratedHosts.Add(LocalHostName);
            changed = true;
        }

        return changed;
    }

    private static OfficialSectionSnapshot LoadOfficialSectionSnapshot(string codexHome)
    {
        try
        {
            var history = new SharedHistoryService();
            var sections = history.LoadThreadSections(codexHome)
                .Where(section => !section.IsPinned)
                .ToList();
            var threads = history.Load(codexHome, 10000)
                .Where(thread => Guid.TryParse(thread.Id, out _))
                .ToList();
            var assignmentByThreadId = threads
                .GroupBy(thread => thread.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var thread = group.First();
                        return thread.Archived ? string.Empty : thread.SectionId;
                    },
                    StringComparer.OrdinalIgnoreCase);
            var orderBySection = history.LoadThreadSectionOrder(codexHome);
            var officialSections = new List<OfficialSection>(sections.Count);
            foreach (var section in sections)
            {
                var orderedThreadIds = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (orderBySection.TryGetValue(section.Id, out var storedOrder))
                {
                    foreach (var threadId in storedOrder)
                    {
                        if (assignmentByThreadId.TryGetValue(threadId, out var assignedSectionId) &&
                            assignedSectionId.Equals(section.Id, StringComparison.OrdinalIgnoreCase) &&
                            seen.Add(threadId))
                        {
                            orderedThreadIds.Add(threadId);
                        }
                    }
                }

                foreach (var thread in threads
                             .Where(thread => !thread.Archived &&
                                              thread.SectionId.Equals(
                                                  section.Id,
                                                  StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(thread => thread.UpdatedAt)
                             .ThenByDescending(thread => thread.Id, StringComparer.OrdinalIgnoreCase))
                {
                    if (seen.Add(thread.Id))
                    {
                        orderedThreadIds.Add(thread.Id);
                    }
                }

                officialSections.Add(new OfficialSection(
                    section.Id,
                    section.Name,
                    section.Appearance,
                    orderedThreadIds.Select(ToLocalThreadKey).ToList()));
            }

            return new OfficialSectionSnapshot(
                true,
                officialSections,
                assignmentByThreadId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Directory-cache sharing must remain available even while Codex is replacing or
            // locking state_5.sqlite.  A later watcher pass will retry the authoritative merge.
            ManagerLifecycleDiagnostics.WriteException(
                "chat-section-official-snapshot-unavailable",
                ex);
            return OfficialSectionSnapshot.Unavailable;
        }
    }

    private static bool ReconcileOfficialProfile(
        JsonObject targetProfile,
        OfficialSectionSnapshot snapshot,
        IReadOnlySet<string> deletedThreadIds,
        ref int addedSections,
        ref int addedItems)
    {
        var targetSections = GetOrCreateArray(targetProfile, SectionsStateName);
        var targetIds = targetSections
            .OfType<JsonObject>()
            .Select(section => ReadString(section, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var officialById = snapshot.Sections.ToDictionary(
            section => section.Id,
            StringComparer.OrdinalIgnoreCase);
        var targetByOfficialId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var targetByName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in targetSections.OfType<JsonObject>())
        {
            EnsureSectionId(section);
            var name = ReadString(section, "name");
            if (!string.IsNullOrWhiteSpace(name) && !targetByName.ContainsKey(name))
            {
                targetByName[name] = section;
            }
            var officialId = ReadHostSectionId(section);
            if (!string.IsNullOrWhiteSpace(officialId) &&
                !targetByOfficialId.ContainsKey(officialId))
            {
                targetByOfficialId[officialId] = section;
            }
        }

        var changed = false;
        foreach (var official in snapshot.Sections)
        {
            if (!targetByOfficialId.TryGetValue(official.Id, out var targetSection) &&
                !targetByName.TryGetValue(official.Name, out targetSection))
            {
                var profileSectionId = NewSectionId(targetIds);
                targetSection = new JsonObject
                {
                    ["id"] = profileSectionId,
                    ["name"] = official.Name,
                    [HostSectionIdsStateName] = new JsonObject
                    {
                        [LocalHostName] = official.Id
                    },
                    [ItemKeysStateName] = new JsonArray(),
                    ["appearance"] = string.IsNullOrWhiteSpace(official.Appearance)
                        ? null
                        : official.Appearance
                };
                targetSections.Add(targetSection);
                targetByOfficialId[official.Id] = targetSection;
                targetByName[official.Name] = targetSection;
                addedSections++;
                changed = true;
            }

            changed |= SetString(targetSection, "name", official.Name);
            var hostSectionIds = GetOrCreateObject(targetSection, HostSectionIdsStateName);
            changed |= SetString(hostSectionIds, LocalHostName, official.Id);
            if (!string.IsNullOrWhiteSpace(official.Appearance))
            {
                changed |= SetString(targetSection, "appearance", official.Appearance);
            }
        }

        // A section removed through the official app-server must also disappear from every
        // OAuth presentation bucket.  Only mapped local sections are removed; account-specific
        // or future non-local sections are preserved.
        for (var index = targetSections.Count - 1; index >= 0; index--)
        {
            if (targetSections[index] is not JsonObject section)
            {
                continue;
            }
            var officialId = ReadHostSectionId(section);
            if (string.IsNullOrWhiteSpace(officialId) ||
                officialId.Equals(CodexAppServerClient.PinnedSectionId, StringComparison.OrdinalIgnoreCase) ||
                officialById.ContainsKey(officialId))
            {
                continue;
            }
            targetSections.RemoveAt(index);
            changed = true;
        }

        foreach (var targetSection in targetSections.OfType<JsonObject>())
        {
            var officialId = ReadHostSectionId(targetSection);
            if (string.IsNullOrWhiteSpace(officialId))
            {
                var name = ReadString(targetSection, "name");
                var match = snapshot.Sections.FirstOrDefault(section =>
                    section.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    officialId = match.Id;
                    changed |= SetString(
                        GetOrCreateObject(targetSection, HostSectionIdsStateName),
                        LocalHostName,
                        officialId);
                }
            }

            var desiredItems = !string.IsNullOrWhiteSpace(officialId) &&
                               officialById.TryGetValue(officialId, out var official)
                ? official.ItemKeys
                : [];
            changed |= ReconcileOfficialItemKeys(
                targetSection,
                officialId,
                desiredItems,
                snapshot.AssignmentByThreadId,
                deletedThreadIds,
                ref addedItems);
        }

        changed |= RemoveMissingSectionReferences(targetProfile, targetSections);
        changed |= EnsureEverySectionIsOrdered(targetProfile, targetSections);
        return changed;
    }

    private static bool ReconcileOfficialItemKeys(
        JsonObject targetSection,
        string officialSectionId,
        IReadOnlyList<string> desiredOfficialItems,
        IReadOnlyDictionary<string, string> assignmentByThreadId,
        IReadOnlySet<string> deletedThreadIds,
        ref int addedItems)
    {
        var currentArray = GetOrCreateArray(targetSection, ItemKeysStateName);
        var current = currentArray
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var key) ? key : string.Empty)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToList();
        var desired = new List<string>(current.Count + desiredOfficialItems.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var itemKey in current)
        {
            if (deletedThreadIds.Any(id => itemKey.Contains(id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (TryReadLocalThreadId(itemKey, out var threadId) &&
                assignmentByThreadId.ContainsKey(threadId))
            {
                // Every known local thread is re-added below only to its authoritative section.
                continue;
            }
            if (seen.Add(itemKey))
            {
                desired.Add(itemKey);
            }
        }

        foreach (var itemKey in desiredOfficialItems)
        {
            if (!TryReadLocalThreadId(itemKey, out var threadId) ||
                !assignmentByThreadId.TryGetValue(threadId, out var assignedSectionId) ||
                !assignedSectionId.Equals(officialSectionId, StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(itemKey))
            {
                continue;
            }
            desired.Add(itemKey);
        }

        if (current.SequenceEqual(desired, StringComparer.Ordinal))
        {
            return false;
        }

        var currentSet = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        addedItems += desired.Count(item => !currentSet.Contains(item));
        currentArray.Clear();
        foreach (var itemKey in desired)
        {
            currentArray.Add(itemKey);
        }
        return true;
    }

    private static bool RemoveMissingSectionReferences(
        JsonObject targetProfile,
        JsonArray targetSections)
    {
        var validIds = targetSections
            .OfType<JsonObject>()
            .Select(section => ReadString(section, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var collapsed = GetOrCreateArray(targetProfile, CollapsedSectionIdsStateName);
        for (var index = collapsed.Count - 1; index >= 0; index--)
        {
            if (collapsed[index] is JsonValue value &&
                value.TryGetValue<string>(out var id) &&
                !validIds.Contains(id))
            {
                collapsed.RemoveAt(index);
                changed = true;
            }
        }

        var order = GetOrCreateArray(targetProfile, SectionOrderStateName);
        for (var index = order.Count - 1; index >= 0; index--)
        {
            if (order[index] is not JsonValue value ||
                !value.TryGetValue<string>(out var key) ||
                !key.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!validIds.Contains(key["custom:".Length..]))
            {
                order.RemoveAt(index);
                changed = true;
            }
        }
        return changed;
    }

    private static bool EnsureEverySectionIsOrdered(
        JsonObject targetProfile,
        JsonArray targetSections)
    {
        var order = GetOrCreateArray(targetProfile, SectionOrderStateName);
        var seen = order
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var key) ? key : string.Empty)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var section in targetSections.OfType<JsonObject>())
        {
            var id = ReadString(section, "id");
            var key = "custom:" + id;
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(key))
            {
                order.Add(key);
                changed = true;
            }
        }
        return changed;
    }

    private static string ReadHostSectionId(JsonObject section)
    {
        return section[HostSectionIdsStateName] is JsonObject hostSectionIds
            ? ReadString(hostSectionIds, LocalHostName)
            : string.Empty;
    }

    private static bool SetString(JsonObject target, string propertyName, string value)
    {
        if (ReadString(target, propertyName).Equals(value, StringComparison.Ordinal))
        {
            return false;
        }
        target[propertyName] = value;
        return true;
    }

    private static string ToLocalThreadKey(string threadId) => LocalThreadKeyPrefix + threadId;

    private static bool TryReadLocalThreadId(string itemKey, out string threadId)
    {
        threadId = string.Empty;
        if (!itemKey.StartsWith(LocalThreadKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var candidate = itemKey[LocalThreadKeyPrefix.Length..];
        if (!Guid.TryParse(candidate, out var parsed))
        {
            return false;
        }
        threadId = parsed.ToString("D");
        return true;
    }

    private static bool RewriteSectionOrder(
        JsonObject targetProfile,
        TemplateProfile template,
        IReadOnlyDictionary<string, JsonObject> targetByName)
    {
        var existingOrder = GetOrCreateArray(targetProfile, SectionOrderStateName);
        var desired = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceKey in template.Order)
        {
            var sourceId = sourceKey.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)
                ? sourceKey["custom:".Length..]
                : sourceKey;
            if (!template.NameById.TryGetValue(sourceId, out var name) ||
                !targetByName.TryGetValue(name, out var targetSection))
            {
                continue;
            }
            var targetId = ReadString(targetSection, "id");
            var targetKey = "custom:" + targetId;
            if (!string.IsNullOrWhiteSpace(targetId) && seen.Add(targetKey))
            {
                desired.Add(targetKey);
            }
        }

        foreach (var sourceSection in template.Sections)
        {
            if (!targetByName.TryGetValue(sourceSection.Name, out var targetSection))
            {
                continue;
            }
            var targetId = ReadString(targetSection, "id");
            var targetKey = "custom:" + targetId;
            if (!string.IsNullOrWhiteSpace(targetId) && seen.Add(targetKey))
            {
                desired.Add(targetKey);
            }
        }

        foreach (var node in existingOrder)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var key) &&
                !string.IsNullOrWhiteSpace(key) && seen.Add(key))
            {
                desired.Add(key);
            }
        }

        var current = existingOrder
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var key) ? key : "")
            .ToList();
        if (current.SequenceEqual(desired, StringComparer.Ordinal))
        {
            return false;
        }
        existingOrder.Clear();
        foreach (var key in desired)
        {
            existingOrder.Add(key);
        }
        return true;
    }

    private static bool RewriteCollapsedSections(
        JsonObject targetProfile,
        TemplateProfile template,
        IReadOnlyDictionary<string, JsonObject> targetByName)
    {
        var collapsed = GetOrCreateArray(targetProfile, CollapsedSectionIdsStateName);
        var sourceCollapsed = template.CollapsedIds;
        var desired = new List<string>();
        var desiredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceSection in template.Sections)
        {
            if (!sourceCollapsed.Contains(sourceSection.Id) ||
                !targetByName.TryGetValue(sourceSection.Name, out var targetSection))
            {
                continue;
            }
            var targetId = ReadString(targetSection, "id");
            if (!string.IsNullOrWhiteSpace(targetId) && desiredSet.Add(targetId))
            {
                desired.Add(targetId);
            }
        }

        var targetNames = new HashSet<string>(template.Sections.Select(section => section.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var node in collapsed)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var id))
            {
                continue;
            }
            var targetSection = targetByName.Values.FirstOrDefault(section =>
                ReadString(section, "id").Equals(id, StringComparison.OrdinalIgnoreCase));
            if (targetSection != null &&
                !targetNames.Contains(ReadString(targetSection, "name")) &&
                desiredSet.Add(id))
            {
                desired.Add(id);
            }
        }

        var current = collapsed
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var id) ? id : "")
            .ToList();
        if (current.SequenceEqual(desired, StringComparer.Ordinal))
        {
            return false;
        }
        collapsed.Clear();
        foreach (var id in desired)
        {
            collapsed.Add(id);
        }
        return true;
    }

    private static List<string> FilterItemKeys(
        IEnumerable<string> itemKeys,
        IReadOnlySet<string> deletedThreadIds,
        IReadOnlySet<string>? availableThreadKeys = null)
    {
        return itemKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Where(key => key.StartsWith("codex:thread:local:", StringComparison.OrdinalIgnoreCase))
            .Where(key => availableThreadKeys == null || availableThreadKeys.Contains(key))
            .Where(key => !deletedThreadIds.Any(id =>
                key.Contains(id, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReplaceItemKeys(
        JsonObject section,
        IReadOnlyList<string> itemKeys,
        out int copiedItems)
    {
        var array = new JsonArray();
        foreach (var itemKey in itemKeys)
        {
            array.Add(itemKey);
        }
        section[ItemKeysStateName] = array;
        copiedItems = itemKeys.Count;
    }

    private static string NewSectionId(ISet<string> existingIds)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("D");
        }
        while (!existingIds.Add(id));
        return id;
    }

    private static void EnsureSectionId(JsonObject section)
    {
        if (!Guid.TryParse(ReadString(section, "id"), out _))
        {
            section["id"] = Guid.NewGuid().ToString("D");
        }
    }

    private static TemplateProfile? SelectTemplate(
        IReadOnlyList<OAuthIdentity> oauthAccounts,
        IReadOnlyDictionary<string, TemplateProfile> profiles,
        string? preferredAccountName)
    {
        var preferred = oauthAccounts.FirstOrDefault(account =>
            !string.IsNullOrWhiteSpace(preferredAccountName) &&
            account.Name.Equals(preferredAccountName, StringComparison.OrdinalIgnoreCase));
        if (preferred != null && profiles.TryGetValue(preferred.AccountId, out var preferredProfile) &&
            preferredProfile.Sections.Count > 0)
        {
            return preferredProfile;
        }

        return profiles.Values
            .Where(profile => profile.Sections.Any())
            // Prefer the populated profile, not merely the one with the most
            // headings.  Older accounts can retain one-off test folders while
            // the active template contains the real classified conversations.
            .OrderByDescending(profile => profile.Sections.Sum(section => section.ItemKeys.Count))
            .ThenByDescending(profile => profile.Sections.Count)
            .ThenBy(profile => profile.AccountId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static Dictionary<string, TemplateProfile> ReadProfiles(JsonObject profiles)
    {
        var result = new Dictionary<string, TemplateProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in profiles)
        {
            if (property.Value is not JsonObject profileState ||
                !Guid.TryParse(property.Key, out _) &&
                !property.Key.Equals(LocalProfileAccountId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sections = new List<TemplateSection>();
            if (profileState[SectionsStateName] is JsonArray sectionArray)
            {
                foreach (var node in sectionArray.OfType<JsonObject>())
                {
                    var id = ReadString(node, "id");
                    var name = ReadString(node, "name");
                    if (!Guid.TryParse(id, out _) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    var items = node[ItemKeysStateName] is JsonArray itemArray
                        ? itemArray.OfType<JsonValue>()
                            .Select(value => value.TryGetValue<string>(out var item) ? item : "")
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .ToList()
                        : [];
                    sections.Add(new TemplateSection(id, name, node, items));
                }
            }

            var collapsed = profileState[CollapsedSectionIdsStateName] is JsonArray collapsedArray
                ? collapsedArray.OfType<JsonValue>()
                    .Select(value => value.TryGetValue<string>(out var id) ? id : "")
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = profileState[SectionOrderStateName] is JsonArray orderArray
                ? orderArray.OfType<JsonValue>()
                    .Select(value => value.TryGetValue<string>(out var key) ? key : "")
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .ToList()
                : [];
            var nameById = sections.ToDictionary(section => section.Id, section => section.Name, StringComparer.OrdinalIgnoreCase);
            result[property.Key] = new TemplateProfile(property.Key, sections, collapsed, order, nameById);
        }
        return result;
    }

    private static JsonObject GetOrCreateProfile(JsonObject profiles, string accountId)
    {
        if (profiles[accountId] is JsonObject existing)
        {
            return existing;
        }

        var profile = new JsonObject
        {
            [SectionsStateName] = new JsonArray(),
            [CollapsedSectionIdsStateName] = new JsonArray(),
            [SectionOrderStateName] = new JsonArray(),
            ["appServerLegacySectionIds"] = new JsonArray(),
            ["appServerMigratedHostIds"] = new JsonArray(LocalHostName)
        };
        profiles[accountId] = profile;
        return profile;
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string name)
    {
        if (parent[name] is JsonArray existing)
        {
            return existing;
        }
        var created = new JsonArray();
        parent[name] = created;
        return created;
    }

    private static string ReadString(JsonObject obj, string propertyName)
    {
        return obj[propertyName] is JsonValue value &&
               value.TryGetValue<string>(out var text)
            ? text ?? ""
            : "";
    }

    private static bool TryReadIdentity(AccountRecord account, out OAuthIdentity identity)
    {
        identity = default!;
        var path = Path.Combine(Path.GetFullPath(account.CodexHome), "auth.json");
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            if (!document.RootElement.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object ||
                !tokens.TryGetProperty("account_id", out var accountIdNode) ||
                accountIdNode.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(accountIdNode.GetString(), out var accountId))
            {
                return false;
            }
            identity = new OAuthIdentity(accountId.ToString("D"), account.Name);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string CreateBackup(string path)
    {
        var backupPath = path + ".sidebar-sync-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
        File.Copy(path, backupPath, overwrite: false);
        return backupPath;
    }

    private static void WriteTextAtomically(string path, string text)
    {
        AtomicFilePersistence.WriteAllText(path, text);
    }

    private static bool TryLoadLocalThreadKeys(
        string codexHome,
        out IReadOnlySet<string> keys)
    {
        keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var history = new SharedHistoryService();
            foreach (var thread in history.Load(codexHome, 10000))
            {
                if (!string.IsNullOrWhiteSpace(thread.Id))
                {
                    ((HashSet<string>)keys).Add(ToLocalThreadKey(thread.Id));
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   JsonException or InvalidOperationException)
        {
            ManagerLifecycleDiagnostics.WriteException(
                "chat-section-local-thread-snapshot-unavailable",
                ex,
                "existing_categories_preserved=true");
            keys = null!;
            return false;
        }
    }

    internal static void Validate()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-section-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var templateId = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var patTargetId = Guid.NewGuid();
            var templateHome = Path.Combine(root, "template");
            var targetHome = Path.Combine(root, "target");
            var patTargetHome = Path.Combine(root, "pat-target");
            Directory.CreateDirectory(templateHome);
            Directory.CreateDirectory(targetHome);
            Directory.CreateDirectory(patTargetHome);
            WriteOAuthAuth(templateHome, templateId);
            WriteOAuthAuth(targetHome, targetId);
            var globalState = new JsonObject
            {
                [PersistedStateName] = new JsonObject
                {
                    [ProfilesStateName] = new JsonObject
                    {
                        [templateId.ToString("D")] = new JsonObject
                        {
                            [SectionsStateName] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = Guid.NewGuid().ToString("D"),
                                    ["name"] = "Codex Account Manager",
                                    [ItemKeysStateName] = new JsonArray("codex:thread:local:" + Guid.NewGuid().ToString("D"))
                                }
                            },
                            [CollapsedSectionIdsStateName] = new JsonArray(),
                            [SectionOrderStateName] = new JsonArray()
                        }
                    }
                }
            };
            var globalPath = Path.Combine(root, GlobalStateFileName);
            File.WriteAllText(globalPath, globalState.ToJsonString(CompactJson), Encoding.UTF8);
            var accounts = new List<AccountRecord>
            {
                new() { Name = "template", CodexHome = templateHome, AuthKind = AccountAuthKind.OfficialOAuth },
                new() { Name = "target", CodexHome = targetHome, AuthKind = AccountAuthKind.OfficialOAuth },
                new() { Name = "pat-target", CodexHome = patTargetHome, AuthKind = AccountAuthKind.AccessToken }
            };
            var patAccountKey = QuotaAccountIdentity.CreateKey(accounts[2]);
            var identityStore = new PatGatewayAccountIdentityStore(root);
            if (!identityStore.Record(patAccountKey, patTargetId.ToString("D")) ||
                !new PatGatewayAccountIdentityStore(root).Load().TryGetValue(
                    patAccountKey,
                    out var persistedPatId) ||
                !persistedPatId.Equals(patTargetId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "PAT whoami identity cache did not survive a Manager/gateway restart.");
            }
            var result = Synchronize(accounts, root, "template", root);
            if (!result.Changed || result.UpdatedProfiles < 3 || result.AddedSections < 3 ||
                !File.Exists(result.BackupPath ?? ""))
            {
                throw new InvalidOperationException("聊天目录同步自测未写入目标 OAuth 桶。");
            }
            var verify = JsonNode.Parse(File.ReadAllText(globalPath))!.AsObject();
            var target = verify[PersistedStateName]![ProfilesStateName]![targetId.ToString("D")]!.AsObject();
            var patTarget = verify[PersistedStateName]![ProfilesStateName]![patTargetId.ToString("D")]!.AsObject();
            var localTarget = verify[PersistedStateName]![ProfilesStateName]![LocalProfileAccountId]!.AsObject();
            if (target[SectionsStateName]!.AsArray().Count != 1 ||
                target[SectionsStateName]!.AsArray()[0]! ["name"]!.GetValue<string>() != "Codex Account Manager" ||
                patTarget[SectionsStateName]!.AsArray().Count != 1 ||
                patTarget[SectionsStateName]!.AsArray()[0]! ["name"]!.GetValue<string>() != "Codex Account Manager" ||
                localTarget[SectionsStateName]!.AsArray().Count != 1 ||
                localTarget[SectionsStateName]!.AsArray()[0]! ["name"]!.GetValue<string>() != "Codex Account Manager")
            {
                throw new InvalidOperationException("OAuth/PAT/API 本地聊天目录同步自测的目标目录缺失。");
            }
            var second = Synchronize(accounts, root, "template", root);
            if (second.Changed)
            {
                throw new InvalidOperationException("聊天目录同步自测不是幂等的。");
            }

            ValidateOfficialMembershipProjection();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for the temporary self-test directory.
            }
        }
    }

    private static void WriteOAuthAuth(string home, Guid accountId)
    {
        var tokens = new JsonObject
        {
            ["id_token"] = "test-id-token",
            ["access_token"] = "test-access-token",
            ["refresh_token"] = "test-refresh-token",
            ["account_id"] = accountId.ToString("D")
        };
        File.WriteAllText(
            Path.Combine(home, "auth.json"),
            new JsonObject { ["tokens"] = tokens }.ToJsonString(CompactJson),
            Encoding.UTF8);
    }

    private static void ValidateOfficialMembershipProjection()
    {
        var learningSectionId = Guid.NewGuid().ToString("D");
        var mailSectionId = Guid.NewGuid().ToString("D");
        var learningProfileId = Guid.NewGuid().ToString("D");
        var mailProfileId = Guid.NewGuid().ToString("D");
        var threadId = Guid.NewGuid().ToString("D");
        var itemKey = ToLocalThreadKey(threadId);
        var profile = new JsonObject
        {
            [SectionsStateName] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = learningProfileId,
                    ["name"] = "学习",
                    [HostSectionIdsStateName] = new JsonObject { [LocalHostName] = learningSectionId },
                    [ItemKeysStateName] = new JsonArray()
                },
                new JsonObject
                {
                    ["id"] = mailProfileId,
                    ["name"] = "邮件",
                    [HostSectionIdsStateName] = new JsonObject { [LocalHostName] = mailSectionId },
                    [ItemKeysStateName] = new JsonArray(itemKey)
                }
            },
            [CollapsedSectionIdsStateName] = new JsonArray(),
            [SectionOrderStateName] = new JsonArray(
                "custom:" + learningProfileId,
                "custom:" + mailProfileId)
        };
        var snapshot = new OfficialSectionSnapshot(
            true,
            [
                new OfficialSection(learningSectionId, "学习", string.Empty, [itemKey]),
                new OfficialSection(mailSectionId, "邮件", string.Empty, [])
            ],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [threadId] = learningSectionId
            });
        var addedSections = 0;
        var addedItems = 0;
        var changed = ReconcileOfficialProfile(
            profile,
            snapshot,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ref addedSections,
            ref addedItems);
        var sections = profile[SectionsStateName]!.AsArray();
        var learning = sections.OfType<JsonObject>().Single(section =>
            ReadString(section, "name") == "学习");
        var mail = sections.OfType<JsonObject>().Single(section =>
            ReadString(section, "name") == "邮件");
        if (!changed || addedSections != 0 || addedItems != 1 ||
            learning[ItemKeysStateName]!.AsArray().Count != 1 ||
            mail[ItemKeysStateName]!.AsArray().Count != 0)
        {
            throw new InvalidOperationException("官方目录归属没有覆盖 OAuth 侧栏缓存。");
        }

        addedItems = 0;
        if (ReconcileOfficialProfile(
                profile,
                snapshot,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ref addedSections,
                ref addedItems))
        {
            throw new InvalidOperationException("官方目录归属同步不是幂等的。");
        }
    }

    private sealed record OAuthIdentity(string AccountId, string Name);

    private sealed record TemplateProfile(
        string AccountId,
        IReadOnlyList<TemplateSection> Sections,
        IReadOnlySet<string> CollapsedIds,
        IReadOnlyList<string> Order,
        IReadOnlyDictionary<string, string> NameById);

    private sealed record TemplateSection(
        string Id,
        string Name,
        JsonObject Node,
        IReadOnlyList<string> ItemKeys);

    private sealed record OfficialSection(
        string Id,
        string Name,
        string Appearance,
        IReadOnlyList<string> ItemKeys);

    private sealed record OfficialSectionSnapshot(
        bool Available,
        IReadOnlyList<OfficialSection> Sections,
        IReadOnlyDictionary<string, string> AssignmentByThreadId)
    {
        internal static OfficialSectionSnapshot Unavailable { get; } = new(
            false,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
