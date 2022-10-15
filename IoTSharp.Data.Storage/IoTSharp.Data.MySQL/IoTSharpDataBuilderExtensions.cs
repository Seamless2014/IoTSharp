﻿
using IoTSharp;
using IoTSharp.Contracts;
using IoTSharp.Data;
using IoTSharp.Data.MySQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using ShardingCore.Core.ShardingConfigurations;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class IoTSharpDataBuilderExtensions
    {
        public static void  ConfigureMySql(this IServiceCollection services,   string connectionString,int  poolSize , IHealthChecksBuilder checksBuilder, HealthChecksUIBuilder healthChecksUI)
        {
            services.AddEntityFrameworkMySql();
            services.AddSingleton<IDataBaseModelBuilderOptions>(c => new MySqlModelBuilderOptions());
            ServerVersion serverVersion=null;
            try
            {
                serverVersion = ServerVersion.AutoDetect(connectionString);
            }
            catch (Exception ex)
            {
                throw new Exception($"Can't detect MySql server's version ,  {ex.Message} ", ex);
            }
            services.AddSingleton(serverVersion);
            services.AddDbContextPool<ApplicationDbContext>(builder =>
            {
                builder.UseInternalServiceProvider(services.BuildServiceProvider());
                builder.UseMySql(connectionString, serverVersion, s => s.MigrationsAssembly("IoTSharp.Data.MySQL").UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
            }
          , poolSize );
           
            checksBuilder.AddMySql(connectionString, "IoTSharp.Data.MySQL");
            healthChecksUI.AddMySqlStorage(connectionString);
      
        }
        public static void UseMySqlToSharding(this ShardingConfigOptions options)
        {
            options.UseShardingQuery((conStr, builder) =>
            {
                builder.UseMySql(conStr, new MySqlServerVersion(new Version()));
            });
            options.UseShardingTransaction((conn, builder) =>
            {
                builder.UseMySql(conn, new MySqlServerVersion(new Version()));
            });
        }
    }
}
