// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Data;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using IColumnMapping = Microsoft.EntityFrameworkCore.Metadata.IColumnMapping;
using ITableMapping = Microsoft.EntityFrameworkCore.Metadata.ITableMapping;

namespace Microsoft.EntityFrameworkCore.Update;

/// <summary>
///     <para>
///         Represents a conceptual command to the database to insert/update/delete a row.
///     </para>
///     <para>
///         This type is typically used by database providers; it is generally not used in application code.
///     </para>
/// </summary>
/// <remarks>
///     See <see href="https://aka.ms/efcore-docs-providers">Implementation of database providers and extensions</see>
///     for more information and examples.
/// </remarks>
public class ModificationCommand : IModificationCommand, INonTrackedModificationCommand
{
    private readonly Func<string>? _generateParameterName;
    private readonly bool _sensitiveLoggingEnabled;
    private readonly bool _detailedErrorsEnabled;
    private readonly IComparer<IUpdateEntry>? _comparer;
    private readonly List<IUpdateEntry> _entries = new();
    private List<IColumnModification>? _columnModifications;
    private bool _mainEntryAdded;
    private EntityState _entityState;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Update>? _logger;

    /// <summary>
    ///     Initializes a new <see cref="ModificationCommand" /> instance.
    /// </summary>
    /// <param name="modificationCommandParameters">Creation parameters.</param>
    public ModificationCommand(in ModificationCommandParameters modificationCommandParameters)
    {
        Table = modificationCommandParameters.Table;
        TableName = modificationCommandParameters.TableName;
        Schema = modificationCommandParameters.Schema;
        StoreStoredProcedure = modificationCommandParameters.StoreStoredProcedure;
        _generateParameterName = modificationCommandParameters.GenerateParameterName;
        _sensitiveLoggingEnabled = modificationCommandParameters.SensitiveLoggingEnabled;
        _detailedErrorsEnabled = modificationCommandParameters.DetailedErrorsEnabled;
        _comparer = modificationCommandParameters.Comparer;
        _logger = modificationCommandParameters.Logger;
        EntityState = EntityState.Modified;
    }

    /// <summary>
    ///     Initializes a new <see cref="ModificationCommand" /> instance.
    /// </summary>
    /// <param name="modificationCommandParameters">Creation parameters.</param>
    public ModificationCommand(in NonTrackedModificationCommandParameters modificationCommandParameters)
    {
        Table = modificationCommandParameters.Table;
        TableName = modificationCommandParameters.TableName;
        Schema = modificationCommandParameters.Schema;
        _sensitiveLoggingEnabled = modificationCommandParameters.SensitiveLoggingEnabled;
        EntityState = EntityState.Modified;
    }

    /// <inheritdoc />
    public virtual ITable? Table { get; }

    /// <inheritdoc />
    public virtual IStoreStoredProcedure? StoreStoredProcedure { get; }

    /// <inheritdoc />
    public virtual string TableName { get; }

    /// <inheritdoc />
    public virtual string? Schema { get; }

    /// <inheritdoc />
    public virtual IReadOnlyList<IUpdateEntry> Entries
        => _entries;

    /// <inheritdoc />
    public virtual EntityState EntityState
    {
        get => _entityState;
        set => _entityState = value;
    }

    /// <inheritdoc />
    public virtual IColumnBase? RowsAffectedColumn { get; private set; }

    /// <summary>
    ///     The list of <see cref="IColumnModification" /> needed to perform the insert, update, or delete.
    /// </summary>
    public virtual IReadOnlyList<IColumnModification> ColumnModifications
        => NonCapturingLazyInitializer.EnsureInitialized(
            ref _columnModifications, this, static command => command.GenerateColumnModifications());

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    [Conditional("DEBUG")]
    [EntityFrameworkInternal]
    public virtual void AssertColumnsNotInitialized()
    {
        if (_columnModifications != null)
        {
            throw new Exception("_columnModifications have been initialized prematurely");
        }
    }

    /// <inheritdoc />
    public virtual void AddEntry(IUpdateEntry entry, bool mainEntry)
    {
        AssertColumnsNotInitialized();

        switch (entry.EntityState)
        {
            case EntityState.Deleted:
            case EntityState.Modified:
            case EntityState.Added:
                break;
            default:
                if (_sensitiveLoggingEnabled)
                {
                    throw new InvalidOperationException(
                        RelationalStrings.ModificationCommandInvalidEntityStateSensitive(
                            entry.EntityType.DisplayName(),
                            entry.BuildCurrentValuesString(entry.EntityType.FindPrimaryKey()!.Properties),
                            entry.EntityState));
                }

                throw new InvalidOperationException(
                    RelationalStrings.ModificationCommandInvalidEntityState(
                        entry.EntityType.DisplayName(),
                        entry.EntityState));
        }

        if (mainEntry)
        {
            Check.DebugAssert(!_mainEntryAdded, "Only expected a single main entry");

            for (var i = 0; i < _entries.Count; i++)
            {
                ValidateState(entry, _entries[i]);
            }

            _mainEntryAdded = true;
            _entries.Insert(0, entry);

            _entityState = entry.SharedIdentityEntry == null
                ? entry.EntityState
                : entry.SharedIdentityEntry.EntityType == entry.EntityType
                || entry.SharedIdentityEntry.EntityType.GetTableMappings()
                    .Any(m => m.Table.Name == TableName && m.Table.Schema == Schema)
                    ? EntityState.Modified
                    : entry.EntityState;
        }
        else
        {
            if (_mainEntryAdded)
            {
                ValidateState(_entries[0], entry);
            }

            _entries.Add(entry);
        }
    }

    private void ValidateState(IUpdateEntry mainEntry, IUpdateEntry entry)
    {
        var mainEntryState = mainEntry.SharedIdentityEntry == null
            ? mainEntry.EntityState
            : EntityState.Modified;
        if (mainEntryState == EntityState.Modified)
        {
            return;
        }

        var entryState = entry.SharedIdentityEntry == null
            ? entry.EntityState
            : EntityState.Modified;
        if (mainEntryState != entryState)
        {
            if (_sensitiveLoggingEnabled)
            {
                throw new InvalidOperationException(
                    RelationalStrings.ConflictingRowUpdateTypesSensitive(
                        entry.EntityType.DisplayName(),
                        entry.BuildCurrentValuesString(entry.EntityType.FindPrimaryKey()!.Properties),
                        entryState,
                        mainEntry.EntityType.DisplayName(),
                        mainEntry.BuildCurrentValuesString(mainEntry.EntityType.FindPrimaryKey()!.Properties),
                        mainEntryState));
            }

            throw new InvalidOperationException(
                RelationalStrings.ConflictingRowUpdateTypes(
                    entry.EntityType.DisplayName(),
                    entryState,
                    mainEntry.EntityType.DisplayName(),
                    mainEntryState));
        }
    }

    /// <summary>
    ///     Creates a new <see cref="IColumnModification" /> and add it to this command.
    /// </summary>
    /// <param name="columnModificationParameters">Creation parameters.</param>
    /// <returns>The new <see cref="IColumnModification" /> instance.</returns>
    public virtual IColumnModification AddColumnModification(in ColumnModificationParameters columnModificationParameters)
    {
        var modification = CreateColumnModification(columnModificationParameters);

        _columnModifications ??= new List<IColumnModification>();

        _columnModifications.Add(modification);

        return modification;
    }

    /// <summary>
    ///     Creates a new instance that implements <see cref="IColumnModification" /> interface.
    /// </summary>
    /// <param name="columnModificationParameters">Creation parameters.</param>
    /// <returns>The new instance that implements <see cref="IColumnModification" /> interface.</returns>
    protected virtual IColumnModification CreateColumnModification(in ColumnModificationParameters columnModificationParameters)
        => new ColumnModification(columnModificationParameters);

    private sealed class JsonPartialUpdateInfo
    {
        public List<JsonPartialUpdatePathEntry> Path { get; } = new();
        public IProperty? Property { get; set; }
        public object? PropertyValue { get; set; }
    }

    private record struct JsonPartialUpdatePathEntry(string PropertyName, int? Ordinal, IUpdateEntry ParentEntry, INavigation Navigation);

    private List<IColumnModification> GenerateColumnModifications()
    {
        var state = EntityState;
        var adding = state == EntityState.Added;
        var updating = state == EntityState.Modified;
        var columnModifications = new List<IColumnModification>();
        Dictionary<string, ColumnValuePropagator>? sharedTableColumnMap = null;
        var jsonEntry = false;

        if (_entries.Count > 1
            || _entries is [{ SharedIdentityEntry: not null }])
        {
            Check.DebugAssert(StoreStoredProcedure is null, "Multiple entries/shared identity not supported with stored procedures");

            sharedTableColumnMap = new Dictionary<string, ColumnValuePropagator>();

            if (_comparer != null)
            {
                _entries.Sort(_comparer);
            }

            foreach (var entry in _entries)
            {
                var tableMapping = GetTableMapping(entry.EntityType);
                if (tableMapping == null)
                {
                    continue;
                }

                if (entry.SharedIdentityEntry != null)
                {
                    var sharedTableMapping = entry.EntityType != entry.SharedIdentityEntry.EntityType
                        ? GetTableMapping(entry.SharedIdentityEntry.EntityType)
                        : tableMapping;
                    if (sharedTableMapping != null)
                    {
                        InitializeSharedColumns(entry.SharedIdentityEntry, sharedTableMapping, updating, sharedTableColumnMap);
                    }
                }

                InitializeSharedColumns(entry, tableMapping, updating, sharedTableColumnMap);

                if (!jsonEntry && entry.EntityType.IsMappedToJson())
                {
                    jsonEntry = true;
                }
            }
        }

        if (jsonEntry)
        {
            var jsonColumnsUpdateMap = new Dictionary<IColumn, JsonPartialUpdateInfo>();
            var processedEntries = new List<IUpdateEntry>();
            foreach (var entry in _entries.Where(e => e.EntityType.IsMappedToJson()))
            {
                var jsonColumn = GetTableMapping(entry.EntityType)!.Table.FindColumn(entry.EntityType.GetContainerColumnName()!)!;
                var jsonPartialUpdateInfo = FindJsonPartialUpdateInfo(entry, processedEntries);

                if (jsonPartialUpdateInfo == null)
                {
                    continue;
                }

                if (jsonColumnsUpdateMap.TryGetValue(jsonColumn, out var currentJsonPartialUpdateInfo))
                {
                    jsonPartialUpdateInfo = FindCommonJsonPartialUpdateInfo(
                        currentJsonPartialUpdateInfo,
                        jsonPartialUpdateInfo);
                }

                jsonColumnsUpdateMap[jsonColumn] = jsonPartialUpdateInfo;
            }

            foreach (var (jsonColumn, updateInfo) in jsonColumnsUpdateMap)
            {
                var finalUpdatePathElement = updateInfo.Path.Last();
                var navigation = finalUpdatePathElement.Navigation;
                var jsonColumnTypeMapping = jsonColumn.StoreTypeMapping;
                var navigationValue = finalUpdatePathElement.ParentEntry.GetCurrentValue(navigation);

                var json = default(JsonNode?);
                var jsonPathString = string.Join(
                    ".", updateInfo.Path.Select(x => x.PropertyName + (x.Ordinal != null ? "[" + x.Ordinal + "]" : "")));

                object? singlePropertyValue = default;
                if (updateInfo.Property != null)
                {
                    singlePropertyValue = GenerateValueForSinglePropertyUpdate(updateInfo.Property, updateInfo.PropertyValue);
                    jsonPathString = jsonPathString + "." + updateInfo.Property.GetJsonPropertyName();
                }
                else
                {
                    if (finalUpdatePathElement.Ordinal != null && navigationValue != null)
                    {
                        var i = 0;
                        foreach (var navigationValueElement in (IEnumerable)navigationValue)
                        {
                            if (i == finalUpdatePathElement.Ordinal)
                            {
                                json = CreateJson(
                                    navigationValueElement,
                                    finalUpdatePathElement.ParentEntry,
                                    navigation.TargetEntityType,
                                    ordinal: null,
                                    isCollection: false);

                                break;
                            }

                            i++;
                        }
                    }
                    else
                    {
                        json = CreateJson(
                            navigationValue,
                            finalUpdatePathElement.ParentEntry,
                            navigation.TargetEntityType,
                            ordinal: null,
                            isCollection: navigation.IsCollection);
                    }
                }

                var columnModificationParameters = new ColumnModificationParameters(
                    jsonColumn.Name,
                    value: updateInfo.Property != null ? singlePropertyValue : json?.ToJsonString(),
                    property: updateInfo.Property,
                    columnType: jsonColumnTypeMapping.StoreType,
                    jsonColumnTypeMapping,
                    jsonPath: jsonPathString,
                    read: false,
                    write: true,
                    key: false,
                    condition: false,
                    _sensitiveLoggingEnabled) { GenerateParameterName = _generateParameterName, };

                columnModifications.Add(new ColumnModification(columnModificationParameters));
            }
        }

        foreach (var entry in _entries.Where(x => !x.EntityType.IsMappedToJson()))
        {
            var nonMainEntry = !_mainEntryAdded || entry != _entries[0];

            var optionalDependentWithAllNull = false;

            if (StoreStoredProcedure is null)
            {
                var tableMapping = GetTableMapping(entry.EntityType);
                if (tableMapping is null)
                {
                    continue;
                }

                optionalDependentWithAllNull =
                    entry.EntityState is EntityState.Modified or EntityState.Added
                    && tableMapping.Table.IsOptional(entry.EntityType)
                    && tableMapping.Table.GetRowInternalForeignKeys(entry.EntityType).Any();

                foreach (var columnMapping in tableMapping.ColumnMappings)
                {
                    HandleColumnModification(columnMapping);
                }
            }
            else // Stored procedure mapping case
            {
                var storedProcedureMapping = GetStoredProcedureMapping(entry.EntityType, EntityState);
                Check.DebugAssert(storedProcedureMapping is not null, "No sproc mapping but StoredProcedure is not null");
                var storedProcedure = storedProcedureMapping.StoredProcedure;

                // Stored procedures may have an additional rows affected result column or return value, which does not have a
                // property/column mapping but still needs to have be represented via a column modification.
                // Note that for rows affected parameters/result columns, we add column modifications below along with regular parameters/
                // result columns; for return value we do that here.
                if (storedProcedure.FindRowsAffectedParameter() is { } rowsAffectedParameter)
                {
                    RowsAffectedColumn = rowsAffectedParameter.StoreParameter;
                }
                else if (storedProcedure.FindRowsAffectedResultColumn() is { } rowsAffectedResultColumn)
                {
                    RowsAffectedColumn = rowsAffectedResultColumn.StoreResultColumn;
                }
                else if (storedProcedureMapping.StoreStoredProcedure.ReturnValue is { } rowsAffectedReturnValue)
                {
                    RowsAffectedColumn = rowsAffectedReturnValue;

                    columnModifications.Add(
                        CreateColumnModification(
                            new ColumnModificationParameters(
                                entry: null,
                                property: null,
                                rowsAffectedReturnValue,
                                _generateParameterName!,
                                rowsAffectedReturnValue.StoreTypeMapping,
                                valueIsRead: true,
                                valueIsWrite: false,
                                columnIsKey: false,
                                columnIsCondition: false,
                                _sensitiveLoggingEnabled)));
                }

                // In TPH, the sproc has parameters for all entity types in the hierarchy; we must generate null column modifications
                // for parameters for unrelated entity types.
                // Enumerate over the sproc parameters in order, trying to match a corresponding parameter mapping.
                // Note that we produce the column modifications in the same order as their sproc parameters; this is important and assumed
                // later in the pipeline.
                foreach (var parameter in StoreStoredProcedure.Parameters)
                {
                    if (parameter.FindParameterMapping(entry.EntityType) is { } parameterMapping)
                    {
                        HandleColumnModification(parameterMapping);
                        continue;
                    }

                    // The parameter has no corresponding mapping; this is either a sibling property in a TPH hierarchy or a rows affected
                    // output parameter. Note that we set IsRead to false since we don't propagate the output parameter.
                    columnModifications.Add(
                        CreateColumnModification(
                            new ColumnModificationParameters(
                                entry: null,
                                property: null,
                                parameter,
                                _generateParameterName!,
                                parameter.StoreTypeMapping,
                                valueIsRead: false,
                                valueIsWrite: parameter.Direction.HasFlag(ParameterDirection.Input),
                                columnIsKey: false,
                                columnIsCondition: false,
                                _sensitiveLoggingEnabled)));
                }

                foreach (var resultColumn in StoreStoredProcedure.ResultColumns)
                {
                    if (resultColumn.FindColumnMapping(entry.EntityType) is { } resultColumnMapping)
                    {
                        HandleColumnModification(resultColumnMapping);
                        continue;
                    }

                    // The result column has no corresponding mapping; this is either a sibling property in a TPH hierarchy or a rows
                    // affected result column. Note that we set IsRead to false since we don't propagate the result column.
                    columnModifications.Add(
                        CreateColumnModification(
                            new ColumnModificationParameters(
                                entry: null,
                                property: null,
                                resultColumn,
                                _generateParameterName!,
                                resultColumn.StoreTypeMapping,
                                valueIsRead: false,
                                valueIsWrite: false,
                                columnIsKey: false,
                                columnIsCondition: false,
                                _sensitiveLoggingEnabled)));
                }
            }

            if (optionalDependentWithAllNull && _logger != null)
            {
                if (_sensitiveLoggingEnabled)
                {
                    _logger.OptionalDependentWithAllNullPropertiesWarningSensitive(entry);
                }
                else
                {
                    _logger.OptionalDependentWithAllNullPropertiesWarning(entry);
                }
            }

            void HandleColumnModification(IColumnMappingBase columnMapping)
            {
                var property = columnMapping.Property;
                var column = columnMapping.Column;
                var storedProcedureParameter = columnMapping is IStoredProcedureParameterMapping parameterMapping
                    ? parameterMapping.Parameter
                    : null;
                var isKey = property.IsPrimaryKey();
                var isCondition = !adding
                    && (isKey
                        || storedProcedureParameter is { ForOriginalValue: true }
                        || (property.IsConcurrencyToken && storedProcedureParameter is null));

                // Store-generated properties generally need to be read back (unless we're deleting).
                // One exception is if the property is mapped to a non-output parameter.
                var readValue = state != EntityState.Deleted
                    && entry.IsStoreGenerated(property)
                    && (storedProcedureParameter is null || storedProcedureParameter.Direction.HasFlag(ParameterDirection.Output));

                ColumnValuePropagator? columnPropagator = null;
                sharedTableColumnMap?.TryGetValue(column.Name, out columnPropagator);

                var writeValue = false;
                if (!readValue)
                {
                    if (adding)
                    {
                        writeValue = property.GetBeforeSaveBehavior() == PropertySaveBehavior.Save;
                    }
                    else if (((updating && property.GetAfterSaveBehavior() == PropertySaveBehavior.Save)
                                 || (!isKey && nonMainEntry)
                                 || entry.SharedIdentityEntry != null)
                             && storedProcedureParameter is not { ForOriginalValue: true })
                    {
                        // Note that for stored procedures we always need to send all parameters, regardless of whether the property
                        // actually changed.
                        writeValue = columnPropagator?.TryPropagate(columnMapping, entry)
                            ?? (entry.EntityState == EntityState.Added || entry.IsModified(property) || StoreStoredProcedure is not null);
                    }
                }

                if (readValue
                    || writeValue
                    || isCondition)
                {
                    var columnModificationParameters = new ColumnModificationParameters(
                        entry,
                        property,
                        column,
                        _generateParameterName!,
                        columnMapping.TypeMapping,
                        readValue,
                        writeValue,
                        isKey,
                        isCondition,
                        _sensitiveLoggingEnabled);

                    var columnModification = CreateColumnModification(columnModificationParameters);

                    if (columnPropagator != null
                        && column.PropertyMappings.Count != 1)
                    {
                        if (columnPropagator.ColumnModification != null)
                        {
                            columnPropagator.ColumnModification.AddSharedColumnModification(columnModification);

                            return;
                        }

                        columnPropagator.ColumnModification = columnModification;
                    }

                    columnModifications.Add(columnModification);

                    if (optionalDependentWithAllNull
                        && (columnModification.IsWrite
                            || (columnModification.IsCondition && !isKey))
                        && columnModification.Value is not null)
                    {
                        optionalDependentWithAllNull = false;
                    }
                }
                else if (optionalDependentWithAllNull
                         && state == EntityState.Modified
                         && entry.GetCurrentValue(property) is not null)
                {
                    optionalDependentWithAllNull = false;
                }
            }
        }

        return columnModifications;

        static JsonPartialUpdateInfo? FindJsonPartialUpdateInfo(IUpdateEntry entry, List<IUpdateEntry> processedEntries)
        {
            var result = new JsonPartialUpdateInfo();
            var currentEntry = entry;
            var currentOwnership = currentEntry.EntityType.FindOwnership()!;

            while (currentEntry.EntityType.IsMappedToJson())
            {
                var jsonPropertyName = currentEntry.EntityType.GetJsonPropertyName()!;
                currentOwnership = currentEntry.EntityType.FindOwnership()!;
                var previousEntry = currentEntry;
#pragma warning disable EF1001 // Internal EF Core API usage.
                currentEntry = ((InternalEntityEntry)currentEntry).StateManager.FindPrincipal(
                    (InternalEntityEntry)currentEntry, currentOwnership)!;
#pragma warning restore EF1001 // Internal EF Core API usage.

                if (processedEntries.Contains(currentEntry))
                {
                    return null;
                }

                var ordinal = default(int?);
                if (!currentOwnership.IsUnique
                    && previousEntry.EntityState != EntityState.Added
                    && previousEntry.EntityState != EntityState.Deleted)
                {
                    var ordinalProperty = previousEntry.EntityType.FindPrimaryKey()!.Properties.Last();
                    ordinal = (int)previousEntry.GetCurrentProviderValue(ordinalProperty)! - 1;
                }

                var pathEntry = new JsonPartialUpdatePathEntry(
                    currentOwnership.PrincipalEntityType.IsMappedToJson() ? jsonPropertyName : "$",
                    ordinal,
                    currentEntry,
                    currentOwnership.GetNavigation(pointsToPrincipal: false)!);

                result.Path.Insert(0, pathEntry);
            }

            var modifiedMembers = entry.ToEntityEntry().Properties.Where(m => m.IsModified).ToList();
            if (modifiedMembers.Count == 1)
            {
                result.Property = modifiedMembers.Single().Metadata;
                result.PropertyValue = entry.GetCurrentProviderValue(result.Property);
            }
            else
            {
                // only add to processed entries list if we are planning to update the entire entity
                // (rather than just a single property on that entity)
                processedEntries.Add(entry);
            }

            // parent entity got deleted, no need to do any json-specific processing
            if (currentEntry.EntityState == EntityState.Deleted)
            {
                return null;
            }

            return result;
        }

        static JsonPartialUpdateInfo FindCommonJsonPartialUpdateInfo(
            JsonPartialUpdateInfo first,
            JsonPartialUpdateInfo second)
        {
            var result = new JsonPartialUpdateInfo();
            for (var i = 0; i < Math.Min(first.Path.Count, second.Path.Count); i++)
            {
                if (first.Path[i].PropertyName == second.Path[i].PropertyName)
                {
                    if (first.Path[i].Ordinal == second.Path[i].Ordinal)
                    {
                        result.Path.Add(first.Path[i]);
                        continue;
                    }

                    var common = new JsonPartialUpdatePathEntry(
                        first.Path[i].PropertyName,
                        null,
                        first.Path[i].ParentEntry,
                        first.Path[i].Navigation);

                    result.Path.Add(common);

                    break;
                }
            }

            Check.DebugAssert(result.Path.Count > 0, "Common denominator should always have at least one node - the root.");

            return result;
        }
    }

    /// <summary>
    ///     Generates value to use for update in case a single property is being updated.
    /// </summary>
    /// <param name="property">Property to be updated.</param>
    /// <param name="propertyValue">Value object that the property will be updated to.</param>
    /// <returns>Value that the property will be updated to.</returns>
    [EntityFrameworkInternal]
    protected virtual object? GenerateValueForSinglePropertyUpdate(IProperty property, object? propertyValue)
    {
        var propertyProviderClrType = (property.GetTypeMapping().Converter?.ProviderClrType ?? property.ClrType).UnwrapNullableType();

        return (propertyProviderClrType == typeof(DateTime)
            || propertyProviderClrType == typeof(DateTimeOffset)
            || propertyProviderClrType == typeof(TimeSpan)
            || propertyProviderClrType == typeof(Guid))
            ? JsonValue.Create(propertyValue)?.ToJsonString().Replace("\"", "")
            : propertyValue;
    }

    private JsonNode? CreateJson(object? navigationValue, IUpdateEntry parentEntry, IEntityType entityType, int? ordinal, bool isCollection)
    {
        if (navigationValue == null)
        {
            return isCollection ? new JsonArray() : null;
        }

        if (isCollection)
        {
            var i = 1;
            var jsonNodes = new List<JsonNode?>();
            foreach (var collectionElement in (IEnumerable)navigationValue)
            {
                // TODO: should we ever expect null entities inside a collection?
                var collectionElementJson = CreateJson(collectionElement, parentEntry, entityType, i++, isCollection: false);
                jsonNodes.Add(collectionElementJson);
            }

            return new JsonArray(jsonNodes.ToArray());
        }

#pragma warning disable EF1001 // Internal EF Core API usage.
        var entry = (IUpdateEntry)((InternalEntityEntry)parentEntry).StateManager.TryGetEntry(navigationValue, entityType)!;
#pragma warning restore EF1001 // Internal EF Core API usage.

        var jsonNode = new JsonObject();
        foreach (var property in entityType.GetProperties())
        {
            if (property.IsKey())
            {
                if (property.IsOrdinalKeyProperty() && ordinal != null)
                {
                    entry.SetStoreGeneratedValue(property, ordinal.Value, setModified: false);
                }

                continue;
            }

            // jsonPropertyName can only be null for key properties
            var jsonPropertyName = property.GetJsonPropertyName()!;
            var value = entry.GetCurrentProviderValue(property);
            jsonNode[jsonPropertyName] = JsonValue.Create(value);
        }

        foreach (var navigation in entityType.GetNavigations())
        {
            // skip back-references to the parent
            if (navigation.IsOnDependent)
            {
                continue;
            }

            var jsonPropertyName = navigation.TargetEntityType.GetJsonPropertyName()!;
            var ownedNavigationValue = entry.GetCurrentValue(navigation)!;
            var navigationJson = CreateJson(
                ownedNavigationValue,
                entry,
                navigation.TargetEntityType,
                ordinal: null,
                isCollection: navigation.IsCollection);

            jsonNode[jsonPropertyName] = navigationJson;
        }

        return jsonNode;
    }

    private ITableMapping? GetTableMapping(IEntityType entityType)
    {
        foreach (var mapping in entityType.GetTableMappings())
        {
            var table = mapping.Table;
            if (table.Name == TableName
                && table.Schema == Schema)
            {
                return mapping;
            }
        }

        return null;
    }

    private IStoredProcedureMapping? GetStoredProcedureMapping(IEntityType entityType, EntityState entityState)
    {
        var sprocMappings = entityState switch
        {
            EntityState.Added => entityType.GetInsertStoredProcedureMappings(),
            EntityState.Modified => entityType.GetUpdateStoredProcedureMappings(),
            EntityState.Deleted => entityType.GetDeleteStoredProcedureMappings(),

            _ => throw new ArgumentOutOfRangeException(nameof(entityState), entityState, "Invalid EntityState value")
        };

        foreach (var mapping in sprocMappings)
        {
            if (mapping.StoreStoredProcedure == StoreStoredProcedure)
            {
                return mapping;
            }
        }

        return null;
    }

    private static void InitializeSharedColumns(
        IUpdateEntry entry,
        ITableMapping tableMapping,
        bool updating,
        Dictionary<string, ColumnValuePropagator> columnMap)
    {
        foreach (var columnMapping in tableMapping.ColumnMappings)
        {
            if (columnMapping.Property.DeclaringEntityType.IsMappedToJson())
            {
                continue;
            }

            var columnName = columnMapping.Column.Name;
            if (!columnMap.TryGetValue(columnName, out var columnPropagator))
            {
                columnPropagator = new ColumnValuePropagator();
                columnMap.Add(columnName, columnPropagator);
            }

            if (updating)
            {
                columnPropagator.RecordValue(columnMapping, entry);
            }
        }
    }

    /// <inheritdoc />
    public virtual void PropagateResults(RelationalDataReader relationalReader)
    {
        var (seenRegularResultColumn, seenStoredProcedureResultColumn) = (false, false);

        var columnCount = ColumnModifications.Count;

        var readerIndex = -1;
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var columnModification = ColumnModifications[columnIndex];

            if (!columnModification.IsRead)
            {
                continue;
            }

            switch (columnModification.Column)
            {
                case IColumn:
                    // If we're reading a regular result set, then we generated SQL which projects columns out in the order in which they're
                    // listed in ColumnModifications.
                    readerIndex++;
#if DEBUG
                    Check.DebugAssert(!seenStoredProcedureResultColumn, "!seenStoredProcedureResultColumn");
                    seenRegularResultColumn = true;
#endif
                    break;

                case IStoreStoredProcedureResultColumn resultColumn:
                    if (ReferenceEquals(RowsAffectedColumn, resultColumn))
                    {
                        continue;
                    }

                    // For stored procedure result sets, we need to get the column ordering from metadata.
                    readerIndex = resultColumn.Position;
#if DEBUG
                    Check.DebugAssert(!seenRegularResultColumn, "!seenRegularResultColumn");
                    seenStoredProcedureResultColumn = true;
#endif
                    break;

                case IStoreStoredProcedureParameter or IStoreStoredProcedureReturnValue:
                    // Stored procedure output parameters (and return values) are only propagated later, since they're populated only when
                    // the reader is closed.
                    continue;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            Check.DebugAssert(
                columnModification.Property is not null, "No property when propagating results to a readable column modification");

            columnModification.Value =
                columnModification.Property.GetReaderFieldValue(relationalReader, readerIndex, _detailedErrorsEnabled);
        }
    }

    /// <inheritdoc />
    public virtual void PropagateOutputParameters(DbParameterCollection parameterCollection, int baseParameterIndex)
    {
        var columnCount = ColumnModifications.Count;

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var columnModification = ColumnModifications[columnIndex];
            if (columnModification.Property is null
                || !columnModification.IsRead
                || columnModification.Column is not IStoreStoredProcedureParameter storedProcedureParameter)
            {
                continue;
            }

            Check.DebugAssert(
                storedProcedureParameter.Direction != ParameterDirection.Input,
                "Readable column modification has a stored procedure parameter with direction Input");
            Check.DebugAssert(
                columnModification.ParameterName is not null,
                "Readable column modification has a stored procedure parameter without a name");

            columnModification.Value = parameterCollection[baseParameterIndex + storedProcedureParameter.Position].Value;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var result = $"{EntityState}: {TableName}";
        if (_columnModifications == null)
        {
            return result;
        }

        result += "(" + string.Join(", ", _columnModifications.Where(m => m.IsKey).Select(m => m.OriginalValue?.ToString())) + ")";
        return result;
    }

    private sealed class ColumnValuePropagator
    {
        private bool _write;
        private object? _originalValue;
        private object? _currentValue;

        public IColumnModification? ColumnModification { get; set; }

        public void RecordValue(IColumnMapping mapping, IUpdateEntry entry)
        {
            var property = mapping.Property;
            switch (entry.EntityState)
            {
                case EntityState.Modified:
                    if (!_write
                        && entry.IsModified(property))
                    {
                        _write = true;
                        _currentValue = entry.GetCurrentProviderValue(property);
                    }

                    break;
                case EntityState.Added:
                    _currentValue = entry.GetCurrentProviderValue(property);
                    _write = !mapping.Column.ProviderValueComparer.Equals(_originalValue, _currentValue);

                    break;
                case EntityState.Deleted:
                    _originalValue = entry.GetOriginalProviderValue(property);
                    if (!_write
                        && !property.IsPrimaryKey())
                    {
                        _write = true;
                        _currentValue = null;
                    }

                    break;
            }
        }

        public bool TryPropagate(IColumnMappingBase mapping, IUpdateEntry entry)
        {
            var property = mapping.Property;
            if (_write
                && (entry.EntityState == EntityState.Unchanged
                    || (entry.EntityState == EntityState.Modified && !entry.IsModified(property))
                    || (entry.EntityState == EntityState.Added
                        && mapping.Column.ProviderValueComparer.Equals(_originalValue, entry.GetCurrentProviderValue(property)))))
            {
                if (property.GetAfterSaveBehavior() == PropertySaveBehavior.Save
                    || entry.EntityState == EntityState.Added)
                {
                    var value = _currentValue;
                    var converter = property.GetTypeMapping().Converter;
                    if (converter != null)
                    {
                        value = converter.ConvertFromProvider(value);
                    }

                    entry.SetStoreGeneratedValue(property, value);
                }

                return false;
            }

            return _write;
        }
    }
}
