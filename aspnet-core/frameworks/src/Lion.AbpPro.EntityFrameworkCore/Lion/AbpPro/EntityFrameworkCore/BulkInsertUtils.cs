﻿namespace Lion.AbpPro.EntityFrameworkCore
{
    public static class BulkInsertUtils
    {
        public static bool IsOwnedProp(IEntityType entityType, PropertyInfo propInfo)
        {
            var propEntityType = entityType.Model.FindEntityType(propInfo.PropertyType);
            return propEntityType != null && propEntityType.IsOwned();
        }

        public static bool IsNavigationProp(IEntityType entityType, PropertyInfo propInfo)
        {
            return entityType.FindNavigation(propInfo) != null;
        }

        static IEnumerable<DbProp> BuildDbPropsForOwnedType(DbContext context, IEntityType entityType, PropertyInfo propInfo)
        {
            var propEntityType = context.Model.FindEntityType(propInfo.PropertyType, propInfo.Name, entityType);
            if (propEntityType == null)
            {
                propEntityType = context.Model.FindEntityType(propInfo.PropertyType);
            }

            var tableIdentifier = StoreObjectIdentifier.Table(propEntityType.GetTableName()!, null);
            foreach (var subProp in propInfo.PropertyType.GetProperties().Where(p => p.CanRead))
            {
                var subPropEFProp = propEntityType.FindProperty(subProp.Name);
                string subPropColName = subPropEFProp!.FindColumn(tableIdentifier)!.Name;
                DbProp dbProp = new DbProp
                {
                    ColumnName = subPropColName,
                    GetValueFunc = (obj) =>
                    {
                        object? propValue = propInfo.GetValue(obj);
                        if (propValue == null)
                        {
                            return null;
                        }

                        object? subPropValue = subProp.GetValue(propValue);
                        return subPropValue;
                    },
                    PropertyType = subProp.PropertyType,
                    ValueConverter = subPropEFProp.GetValueConverter(),
                };
                yield return dbProp;
            }
        }

        /// <summary>
        /// Get properties except for navigationProperties or  autogenerated ones
        /// </summary>
        public static DbProp[] ParseDbProps<TEntity>(DbContext dbCtx, IEntityType entityType) where TEntity : class
        {
            //skip navigationProperties
            var props = typeof(TEntity).GetProperties().Where(p => IsOwnedProp(entityType, p) || !IsNavigationProp(entityType, p) && p.CanRead);
            List<DbProp> propFields = new List<DbProp>();

            foreach (var prop in props)
            {
                if (IsOwnedProp(entityType, prop))
                {
                    foreach (var p in BuildDbPropsForOwnedType(dbCtx, entityType, prop))
                    {
                        propFields.Add(p);
                    }
                }

                string propName = prop.Name;
                var efProp = entityType.FindProperty(propName);
                if (efProp == null) //this property is not mapped
                {
                    continue;
                }

                //skip the columns those are autogenerated
                if (efProp.ValueGenerated == ValueGenerated.OnAdd
                    || efProp.ValueGenerated == ValueGenerated.OnAddOrUpdate)
                {
                    if (efProp.ClrType != typeof(Guid) && efProp.ClrType != typeof(Guid?))
                    {
                        continue;
                    }
                }

                string dbColName = efProp.GetColumnName(StoreObjectIdentifier.SqlQuery(entityType));
                DbProp dbProp = new DbProp
                {
                    ColumnName = dbColName,
                    //Property = prop,
                    GetValueFunc = (obj) => prop.GetValue(obj),
                    PropertyType = prop.PropertyType,
                    ValueConverter = efProp.GetValueConverter(),
                };
                propFields.Add(dbProp);
            }

            return propFields.ToArray();
        }

        /// <summary>
        /// Build DataTable for items
        /// </summary>
        public static DataTable BuildDataTable<TEntity>(DbContext dbCtx, DbSet<TEntity> dbSet,
            IEnumerable<TEntity> items) where TEntity : class
        {
            var entityType = dbSet.EntityType;
            var dbProps = ParseDbProps<TEntity>(dbCtx, entityType);
            DataTable dataTable = new DataTable();
            foreach (var dbProp in dbProps)
            {
                string columnName = dbProp.ColumnName;
                Type propType = dbProp.PropertyType;
                DataColumn col;
                bool isNullable;
                var valueConverter = dbProp.ValueConverter;
                if (valueConverter != null)
                {
                    var providerType = valueConverter.ProviderClrType;
                    isNullable = BatchUtils.IsNullableType(providerType);
                    if (isNullable)
                    {
                        col = dataTable.Columns.Add(columnName,
                            providerType.GenericTypeArguments[0]);
                        col.AllowDBNull = true;
                    }
                    else
                    {
                        col = dataTable.Columns.Add(columnName, providerType);
                    }
                }
                else
                {
                    isNullable = BatchUtils.IsNullableType(propType);
                    if (isNullable)
                    {
                        col = dataTable.Columns.Add(columnName,
                            propType.GenericTypeArguments[0]);
                        col.AllowDBNull = true;
                    }
                    else
                    {
                        col = dataTable.Columns.Add(columnName, propType);
                    }
                }
            }

            foreach (var item in items)
            {
                DataRow row = dataTable.NewRow();
                foreach (var dbProp in dbProps)
                {
                    //object? value = dbProp.Property.GetValue(item);
                    object? value = dbProp.GetValueFunc(item);


                    var valueConverter = dbProp.ValueConverter;
                    if (valueConverter != null)
                    {
                        value = valueConverter.ConvertToProvider(value);
                    }

                    //ValueConverter end
                    if (value == null)
                    {
                        value = DBNull.Value;
                    }

                    row[dbProp.ColumnName] = value;
                }

                dataTable.Rows.Add(row);
            }

            return dataTable;
        }
    }
}