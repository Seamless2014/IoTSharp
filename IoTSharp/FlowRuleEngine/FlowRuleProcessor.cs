﻿using Castle.Components.DictionaryAdapter;
using EasyCaching.Core;
using IoTSharp.Data;
using IoTSharp.Interpreter;
using IoTSharp.TaskActions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using IoTSharp.Extensions;
using IoTSharp.Contracts;
using IoTSharp.Data.Extensions;

namespace IoTSharp.FlowRuleEngine
{
    public class FlowRuleProcessor
    {
        private readonly IServiceScopeFactory _scopeFactor;
        private readonly ILogger<FlowRuleProcessor> _logger;
        private readonly AppSettings _setting;
        private List<Flow> _allFlows;
        private List<FlowOperation> _allflowoperation;
        private TaskExecutorHelper _helper;
        private readonly IEasyCachingProvider _caching;
        private readonly IServiceProvider _sp;

        private readonly int _maximumiteration = 1000;

        public FlowRuleProcessor(ILogger<FlowRuleProcessor> logger, IServiceScopeFactory scopeFactor, IOptions<AppSettings> options, TaskExecutorHelper helper, IEasyCachingProviderFactory factory)
        {
           
            string _hc_Caching = $"{nameof(CachingUseIn)}-{Enum.GetName(options.Value.CachingUseIn)}";
            _scopeFactor = scopeFactor;
            _logger = logger;
            _setting = options.Value;
            _allFlows = new List<Flow>();
            _allflowoperation = new List<FlowOperation>();
            _helper = helper;
            _caching = factory.GetCachingProvider(_hc_Caching);
            _sp = _scopeFactor.CreateScope().ServiceProvider;
        }

        public async Task RunRules(Guid devid, object obj, EventType mountType)
        {
            try
            {
                var rules = await _caching.GetAsync($"ruleid_{devid}_{Enum.GetName(mountType)}", async () =>
                {
                    using (var scope = _scopeFactor.CreateScope())
                    using (var _dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>())
                    {
                        var guids = await _dbContext.GerDeviceRulesIdList(devid, mountType);
                        return guids;
                    }
                }, TimeSpan.FromSeconds(_setting.RuleCachingExpiration));
                if (rules.HasValue)
                {
                    rules.Value.ToList().ForEach(async g =>
                    {
                        try
                        {
                          await RunFlowRules(g, obj, devid, FlowRuleRunType.Normal, null);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"为设备{devid}执行规则链{g}时遇到错误{ex.Message}");
                        }
                    });
                }
                else
                {
                    _logger.LogDebug($"{devid}的数据无相关规则链处理。");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{devid}处理规则链时遇到异常:{ex.Message}");

            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="ruleid"> 规则Id</param>
        /// <param name="data">数据</param>
        /// <param name="deviceId">创建者(可以是模拟器(测试)，可以是设备，在EventType中区分一下)</param>
        /// <param name="type">类型</param>
        /// <param name="bizId">业务Id(第三方唯一Id，用于取回事件以及记录的标识)</param>
        /// <returns> 返回所有节点的记录信息，需要保存则保存</returns>

        public async Task<List<FlowOperation>> RunFlowRules(Guid ruleid, object data, Guid deviceId, FlowRuleRunType type, string bizId)
        {
            var cacheRule = await _caching.GetAsync($"RunFlowRules_{ruleid}", async () =>
            {
                FlowRule rule;
                List<Flow> allFlows;
                using (var sp = _scopeFactor.CreateScope())
                {
                    using (var context = sp.ServiceProvider.GetRequiredService<ApplicationDbContext>())
                    {
                        rule = await context.FlowRules.AsNoTracking().FirstOrDefaultAsync(c => c.RuleId == ruleid);
                        allFlows = await context.Flows.AsNoTracking().Where(c => c.FlowRule == rule && c.FlowStatus > 0).ToListAsync();
                        _logger.LogInformation($"读取规则链{rule?.Name}({ruleid}),子流程共计:{allFlows.Count}");
                    }
                }
                return (rule, _allFlows: allFlows);
            }, TimeSpan.FromSeconds(_setting.RuleCachingExpiration));
            if (cacheRule.HasValue)
            {
                FlowRule rule = cacheRule.Value.rule;
                _allFlows = cacheRule.Value._allFlows;
                _logger.LogInformation($"开始执行规则链{rule?.Name}({ruleid})");
                var @event = new BaseEvent()
                {
                    CreaterDateTime = DateTime.Now,
                    Creator = deviceId,
                    EventDesc = $"Event Rule:{rule?.Name}({ruleid}) device is {deviceId}",
                    EventName = $"开始执行规则链{rule?.Name}({ruleid})",
                    MataData = JsonConvert.SerializeObject(data),
                    //  BizData = JsonConvert.SerializeObject(rule),  //所有规则修改都会让对应的flow数据和设计文件不一致，最终导致回放失败，在此拷贝一份原始数据
                    FlowRule = rule,
                    Bizid = bizId,
                    Type = type,
                    EventStaus = 1
                };
                using (var sp = _scopeFactor.CreateScope())
                {
                    using (var context = sp.ServiceProvider.GetRequiredService<ApplicationDbContext>())
                    {
                        var r = context.FlowRules.Include(c => c.Customer).Include(c => c.Tenant).AsSplitQuery().FirstOrDefault(c => c.RuleId == rule.RuleId);
                        if (r != null)
                        {
                            @event.FlowRule = r;
                            @event.Tenant = r.Tenant;
                            @event.Customer = r.Customer;
                            context.BaseEvents.Add(@event);
                            context.SaveChanges();
                        }
                    }
                }
                var flows = _allFlows.Where(c => c.FlowType != "label").ToList();
                var start = flows.FirstOrDefault(c => c.FlowType == "bpmn:StartEvent");

                if (start == null)
                {
                    _allflowoperation.Add(new FlowOperation()
                    {
                        OperationId = Guid.NewGuid(),
                        bpmnid = "",
                        AddDate = DateTime.Now,
                        FlowRule = rule,
                        Flow = start,
                        Data = JsonConvert.SerializeObject(data),
                        NodeStatus = 1,
                        OperationDesc = "未能找到启动节点",
                        Step = 1,
                        BaseEvent = @event
                    });

                    return _allflowoperation;
                }
                var startoperation = new FlowOperation()
                {
                    OperationId = Guid.NewGuid(),
                    bpmnid = start.bpmnid,
                    AddDate = DateTime.Now,
                    FlowRule = rule,
                    Flow = start,
                    Data = JsonConvert.SerializeObject(data),
                    NodeStatus = 1,
                    OperationDesc = "进入开始节点",
                    Step = 1,
                    BaseEvent = @event
                };

                _allflowoperation.Add(startoperation);
                var nextflows = await ProcessCondition(start.FlowId, data);
                if (nextflows != null)
                {
                    var step = startoperation.Step + 1;
                    foreach (var item in nextflows)
                    {
                        var flowOperation = new FlowOperation()
                        {
                            OperationId = Guid.NewGuid(),
                            AddDate = DateTime.Now,
                            FlowRule = rule,
                            BaseEvent = @event,
                            Flow = item,
                            Data = JsonConvert.SerializeObject(data),
                            NodeStatus = 1,
                            OperationDesc = "Condition（" + (string.IsNullOrEmpty(item.Conditionexpression)
                                ? "Empty Condition"
                                : item.Conditionexpression) + ")",
                            Step = step,
                            bpmnid = item.bpmnid,
                        };

                        _allflowoperation.Add(flowOperation);
                        await Process(flowOperation.OperationId, data, deviceId);
                    }
                    return _allflowoperation;
                }
            }
            return null;
        }

        public async Task Process(Guid operationid, object data, Guid deviceId)
        {
            var peroperation = _allflowoperation.FirstOrDefault(c => c.OperationId == operationid);
            if (peroperation != null)
            {
                if (peroperation.Step > this._maximumiteration)
                {
                    peroperation.OperationDesc = "Maximum iteration depth has been reached";
                    peroperation.NodeStatus = 3;
                    return;
                }

                var flow = _allFlows.FirstOrDefault(c => c.bpmnid == peroperation.Flow.TargetId && c.FlowType != "label");
                switch (flow.FlowType)
                {
                    case "bpmn:SequenceFlow":
                        {
                            var step = peroperation.Step + 1;
                            var operation = new FlowOperation()
                            {
                                OperationId = Guid.NewGuid(),
                                AddDate = DateTime.Now,
                                FlowRule = peroperation.BaseEvent.FlowRule,
                                Flow = flow,
                                Data = JsonConvert.SerializeObject(data),
                                NodeStatus = 1,
                                OperationDesc = "Condition（" + (string.IsNullOrEmpty(flow.Conditionexpression)
                                    ? "Empty Condition"
                                    : flow.Conditionexpression) + ")",
                                Step = step,
                                bpmnid = flow.bpmnid,
                                BaseEvent = peroperation.BaseEvent
                            };
                            _allflowoperation.Add(operation);
                            await Process(operation.OperationId, data, deviceId);

                        }

                        break;

                    case "bpmn:Task":
                        {
                            var step = peroperation.Step + 1;
                            var taskoperation = new FlowOperation()
                            {
                                OperationId = Guid.NewGuid(),
                                bpmnid = flow.bpmnid,
                                AddDate = DateTime.Now,
                                FlowRule = peroperation.BaseEvent.FlowRule,
                                Flow = flow,
                                Data = JsonConvert.SerializeObject(data),
                                NodeStatus = 1,
                                OperationDesc = "Run" + flow.NodeProcessScriptType + "Task:" + flow.Flowname,
                                Step = step,
                                BaseEvent = peroperation.BaseEvent
                            };
                            _allflowoperation.Add(taskoperation);

                            //脚本处理
                            if (!string.IsNullOrEmpty(flow.NodeProcessScriptType) && (!string.IsNullOrEmpty(flow.NodeProcessScript) || !string.IsNullOrEmpty(flow.NodeProcessClass)))
                            {
                                var scriptsrc = flow.NodeProcessScript;

                                dynamic obj = null;
                                switch (flow.NodeProcessScriptType)
                                {
                                    case "executor":

                                        if (!string.IsNullOrEmpty(flow.NodeProcessClass))
                                        {
                                            TaskAction executor = _helper.CreateInstanceByTypeName(flow.NodeProcessClass);
                                            if (executor != null)
                                            {
                                                try
                                                {
                                                    var result = await executor.ExecuteAsync(new TaskActionInput()
                                                    {
                                                        Input = taskoperation.Data,
                                                        DeviceId = deviceId,
                                                        ExecutorConfig = flow.NodeProcessParams,
                                                    }
                                                    );

                                                    _logger.Log(LogLevel.Information, "执行器" + flow.NodeProcessClass + "已完成处理");
                                                    obj = result.DynamicOutput;
                                                    taskoperation.OperationDesc += "\r\n" + result.ExecutionInfo;
                                                    if (!result.ExecutionStatus)
                                                    {
                                                        taskoperation.NodeStatus = 2;
                                                        string info = JsonConvert.SerializeObject(result.DynamicOutput);
                                                        _logger.Log(LogLevel.Information, "执行器执行失败："+ result.ExecutionInfo +"\r\n"+ flow.NodeProcessClass + "未能正确处理:" + info);
                                                        return;
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    _logger.Log(LogLevel.Information, "执行器" + flow.NodeProcessClass + "未能正确处理:" + ex.Source);

                                                    taskoperation.OperationDesc += "\r\n" + ex.Message;
                                                    taskoperation.NodeStatus = 2;
                                                    return;
                                                }
                                            }
                                            else
                                            {
                                                _logger.Log(LogLevel.Warning, "脚本执行异常,未能实例化执行器");
                                                taskoperation.OperationDesc += "\r\n" + "脚本执行异常,未能实例化执行器";
                                                taskoperation.NodeStatus = 2;
                                                return;
                                            }
                                        }
                                        break;

                                    case "python":
                                        {
                                            using (var pse = _sp.GetRequiredService<PythonScriptEngine>())
                                            {
                                                try
                                                {
                                                    string result = pse.Do(scriptsrc, taskoperation.Data);
                                                    obj = JsonConvert.DeserializeObject<object>(result);
                                                }
                                                catch (Exception ex)
                                                {

                                                    _logger.Log(LogLevel.Warning, "python脚本执行异常");
                                                    taskoperation.OperationDesc += ex.Message;
                                                    taskoperation.NodeStatus = 2;
                                                }
                                            }
                                        }
                                        break;

                                    case "sql":
                                        {
                                            using (var pse = _sp.GetRequiredService<SQLEngine>())
                                            {
                                                try
                                                {
                                                    string result = pse.Do(scriptsrc, taskoperation.Data);
                                                    obj = JsonConvert.DeserializeObject<object>(result);
                                                }
                                                catch (Exception ex)
                                                {

                                                    _logger.Log(LogLevel.Warning, "sql脚本执行异常");
                                                    taskoperation.OperationDesc += ex.Message;
                                                    taskoperation.NodeStatus = 2;
                                                }
                                            }
                                        }

                                        break;

                                    case "lua":
                                        {

                                            using (var lua = _sp.GetRequiredService<LuaScriptEngine>())
                                            {
                                                try
                                                {
                                                    string result = lua.Do(scriptsrc, taskoperation.Data);
                                                    obj = JsonConvert.DeserializeObject<object>(result);
                                                }
                                                catch (Exception ex)
                                                {

                                                    _logger.Log(LogLevel.Warning, "lua脚本执行异常");
                                                    taskoperation.OperationDesc += ex.Message;
                                                    taskoperation.NodeStatus = 2;
                                                }
                                            }

                                        }
                                        break;

                                    case "javascript":
                                        {

                                            using (var js = _sp.GetRequiredService<JavaScriptEngine>())
                                            {
                                                try
                                                {
                                                    string result = js.Do(scriptsrc, taskoperation.Data);
                                                    obj = JsonConvert.DeserializeObject<object>(result);

                                                }
                                                catch (Exception ex)
                                                {

                                                    _logger.Log(LogLevel.Warning, "javascript脚本执行异常");
                                                    taskoperation.OperationDesc += ex.Message;
                                                    taskoperation.NodeStatus = 2;
                                                }

                                            }
                                        }
                                        break;

                                    case "csharp":
                                        {
                                            using (var js = _sp.GetRequiredService<CSharpScriptEngine>())
                                            {

                                                try
                                                {

                                                    string result = js.Do(scriptsrc, taskoperation.Data);
                                                    obj = JsonConvert.DeserializeObject<object>(result);
                                                }
                                                catch (Exception ex)
                                                {

                                                    _logger.Log(LogLevel.Warning, "csharp脚本执行异常");
                                                    _logger.Log(LogLevel.Warning, ex.Message);
                                                    taskoperation.OperationDesc += ex.Message;
                                                    taskoperation.NodeStatus = 2;
                                                }


                                            }
                                        }
                                        break;
                                }

                                if (obj != null)
                                {
                                    var next = await ProcessCondition(taskoperation.Flow.FlowId, obj);
                                    var cstep = taskoperation.Step + 1;
                                    foreach (var item in next)
                                    {
                                        var flowOperation = new FlowOperation()
                                        {
                                            OperationId = Guid.NewGuid(),
                                            AddDate = DateTime.Now,
                                            FlowRule = peroperation.BaseEvent.FlowRule,
                                            Flow = item,
                                            Data = JsonConvert.SerializeObject(obj),
                                            NodeStatus = 1,
                                            OperationDesc = "Execute（" +
                                                            (string.IsNullOrEmpty(item.Conditionexpression)
                                                                ? "Empty Condition"
                                                                : item.Conditionexpression) + ")",
                                            Step = cstep,
                                            bpmnid = item.bpmnid,
                                            BaseEvent = taskoperation.BaseEvent
                                        };
                                        _allflowoperation.Add(flowOperation);
                                        await Process(flowOperation.OperationId, obj, deviceId);
                                    }
                                }
                                else
                                {

                                    taskoperation.NodeStatus = 2;
                                    _logger.Log(LogLevel.Warning, "脚本未能顺利执行");
                                }
                            }
                            else
                            {
                                var next = await ProcessCondition(taskoperation.Flow.FlowId, data);
                                var cstep = taskoperation.Step + 1;
                                foreach (var item in next)
                                {
                                    var flowOperation = new FlowOperation()
                                    {
                                        OperationId = Guid.NewGuid(),
                                        AddDate = DateTime.Now,
                                        FlowRule = peroperation.BaseEvent.FlowRule,
                                        Flow = item,
                                        Data = JsonConvert.SerializeObject(data),
                                        NodeStatus = 1,
                                        OperationDesc = "Execute（" + (string.IsNullOrEmpty(item.Conditionexpression)
                                            ? "Empty Condition"
                                            : item.Conditionexpression) + ")",
                                        Step = cstep,
                                        bpmnid = item.bpmnid,
                                        BaseEvent = taskoperation.BaseEvent
                                    };
                                    _allflowoperation.Add(flowOperation);
                                    await Process(flowOperation.OperationId, data, deviceId);
                                }
                            }
                        }

                        break;

                    case "bpmn:EndEvent":


                        var end = new FlowOperation();
                        end.BuildFlowOperation(peroperation, flow);
                        end.OperationId = Guid.NewGuid();
                        end.bpmnid = flow.bpmnid;
                        end.AddDate = DateTime.Now;
                        end.FlowRule = peroperation.BaseEvent.FlowRule;
                        end.Flow = flow;
                        end.Data = JsonConvert.SerializeObject(data);
                        end.NodeStatus = 1;
                        end.OperationDesc = "处理完成";
                        end.Step = 1 + _allflowoperation.Max(c => c.Step);
                        end.BaseEvent = peroperation.BaseEvent;
                        _allflowoperation.Add(end);

                        _logger.Log(LogLevel.Warning, "规则链执行完成");

                        break;

                    //没有终结点的节点必须留个空标签
                    case "label":

                        break;

                    case "bpmn:Lane":

                        break;

                    case "bpmn:Participant":

                        break;

                    case "bpmn:DataStoreReference":

                        break;

                    case "bpmn:SubProcess":

                        break;

                    default:
                        {
                            break;
                        }
                }
            }
        }

        public async Task<List<Flow>> ProcessCondition(Guid flowId, object data)
        {

            var tt = data.GetType();
            var emptyflow = new List<Flow>();
            var flow = _allFlows.FirstOrDefault(c => c.FlowId == flowId);
            if (flow != null)
            {
                var flows = _allFlows.Where(c => c.SourceId == flow?.bpmnid).ToList();
                emptyflow = flows.Where(c => c.Conditionexpression == string.Empty).ToList() ?? new List<Flow>();
                var tasks = new BaseRuleTask()
                {
                    Name = flow.Flowname,
                    Eventid = flow.bpmnid,
                    id = flow.bpmnid,
                    outgoing = new EditableList<BaseRuleFlow>()
                };
                foreach (var item in flows.Except(emptyflow))
                {
                    var rule = new BaseRuleFlow();
                    rule.id = item.bpmnid;
                    rule.Name = item.bpmnid;
                    rule.Eventid = item.bpmnid;
                    rule.Expression = item.Conditionexpression;
                    tasks.outgoing.Add(rule);
                }
                if (tasks.outgoing.Count > 0)
                {
                    SimpleFlowExcutor flowExcutor = new SimpleFlowExcutor();
                    if (data != null)
                    {
                        if (data.GetType() == typeof(JObject))
                        {
                            var t = data as JObject;
                            var d = t?.ToObject<ExpandoObject>();
                            if (d != null)
                            {
                                var result = await flowExcutor.Excute(new FlowExcuteEntity()
                                {
                                    Params = d,
                                    Task = tasks,
                                });
                                var next = result.Where(c => c.IsSuccess).ToList();
                                foreach (var item in next)
                                {
                                    var nextflow = flows.FirstOrDefault(a => a.bpmnid == item.Rule.SuccessEvent);
                                    emptyflow.Add(nextflow);
                                }
                            }
                            else
                            {
                                _logger.LogWarning($"执行 {flowId}的规则链时遇到data为空。");
                            }
                        } else 
                        if (data.GetType() == typeof(JArray))
                        {
                            var result = await flowExcutor.Excute(new FlowExcuteEntity()
                            {
                                Params = data,
                                Task = tasks,
                            });
                            var next = result.Where(c => c.IsSuccess).ToList();
                            foreach (var item in next)
                            {
                                var nextflow = flows.FirstOrDefault(a => a.bpmnid == item.Rule.SuccessEvent);
                                emptyflow.Add(nextflow);
                            }
                        }
                        else
                        if (data is ExpandoObject)
                        {
                            var result = await flowExcutor.Excute(new FlowExcuteEntity()
                            {
                                Params = data,
                                Task = tasks,
                            });
                            var next = result.Where(c => c.IsSuccess).ToList();
                            foreach (var item in next)
                            {
                                var nextflow = flows.FirstOrDefault(a => a.bpmnid == item.Rule.SuccessEvent);
                                emptyflow.Add(nextflow);
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"执行 {flowId}的规则链时遇到未预期的数据类型:{data.GetType()}");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"执行 {flowId}的规则链时遇到data为空。");
                        var result = await flowExcutor.Excute(new FlowExcuteEntity()
                        {
                            Params = null,
                            Task = tasks,
                        });
                        var next = result.Where(c => c.IsSuccess).ToList();
                        foreach (var item in next)
                        {
                            var nextflow = flows.FirstOrDefault(a => a.bpmnid == item.Rule.SuccessEvent);
                            emptyflow.Add(nextflow);
                        }

                    }


                }
            }
            else
            {
                _logger.LogWarning($"ProcessCondition flowId={flowId}");
            }
            return emptyflow;
        }

        public async Task<ScriptTestResult> TestScript(Guid ruleid, Guid flowId, string data)
        {
            var cacheRule = await _caching.GetAsync($"RunFlowRules_{ruleid}", async () =>
            {
                FlowRule rule;

                using (var sp = _scopeFactor.CreateScope())
                {
                    using (var context = sp.ServiceProvider.GetRequiredService<ApplicationDbContext>())
                    {
                        rule = await context.FlowRules.FirstOrDefaultAsync(c => c.RuleId == ruleid);
                        _allFlows = await context.Flows.Where(c => c.FlowRule == rule && c.FlowStatus > 0).ToListAsync();
                        _logger.LogInformation($"读取规则链{rule?.Name}({ruleid}),子流程共计:{_allFlows.Count}");
                    }
                }
                return (rule, _allFlows);
            }, TimeSpan.FromSeconds(_setting.RuleCachingExpiration));
            if (cacheRule.HasValue)
            {
                var flow = _allFlows.FirstOrDefault(c => c.FlowId == flowId);

                if (!string.IsNullOrEmpty(flow?.NodeProcessScriptType) &&
                    (!string.IsNullOrEmpty(flow.NodeProcessScript) || !string.IsNullOrEmpty(flow.NodeProcessClass)))
                {
                    var scriptsrc = flow.NodeProcessScript;

                    dynamic obj = null;

                    switch (flow.NodeProcessScriptType)
                    {
                        case "executor":

                            if (!string.IsNullOrEmpty(flow.NodeProcessClass))
                            {
                                TaskAction executor = _helper.CreateInstanceByTypeName(flow.NodeProcessClass);
                                if (executor != null)
                                {
                                    try
                                    {
                                        var result = await executor.ExecuteAsync(new TaskActionInput()
                                        {
                                            Input = data,
                                            ExecutorConfig = flow.NodeProcessParams,
                                            DeviceId = Guid.Empty
                                        });
                                        obj = result.DynamicOutput;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning($"执行{flow.NodeProcessClass}失败， {ex.Message}");
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning($"{flow.NodeProcessClass},未找到类型 ");
                                }
                            }
                            break;

                        case "python":
                            {
                                using (var pse = _sp.GetRequiredService<PythonScriptEngine>())
                                {
                                    string result = pse.Do(scriptsrc, data);
                                    obj = JsonConvert.DeserializeObject<object>(result);
                                }
                            }
                            break;

                        case "sql":
                            {
                                using (var pse = _sp.GetRequiredService<SQLEngine>())
                                {
                                    string result = pse.Do(scriptsrc, data);
                                    obj = JsonConvert.DeserializeObject<object>(result);
                                }
                            }

                            break;

                        case "lua":
                            {
                                using (var lua = _sp.GetRequiredService<LuaScriptEngine>())
                                {
                                    string result = lua.Do(scriptsrc, data);
                                    obj = JsonConvert.DeserializeObject<object>(result);
                                }
                            }
                            break;

                        case "javascript":
                            {
                                using (var js = _sp.GetRequiredService<JavaScriptEngine>())
                                {
                                    string result = js.Do(scriptsrc, data);
                                    obj = JsonConvert.DeserializeObject<object>(result);
                                }
                            }
                            break;

                        case "csharp":
                            {
                                using (var js = _sp.GetRequiredService<CSharpScriptEngine>())
                                {
                                    string result = js.Do(scriptsrc, data);
                                    obj = JsonConvert.DeserializeObject<object>(result);
                                }
                            }
                            break;
                    }

                    if (obj != null)
                    {
                        return new ScriptTestResult() { Data = obj, IsExecuted = true };
                    }
                }
            }

            return new ScriptTestResult() { Data = null, IsExecuted = false }; ;
        }

        public async Task<ConditionTestResult> TestCondition(Guid ruleid, Guid flowId, dynamic data)
        {
            var cacheRule = await _caching.GetAsync($"RunFlowRules_{ruleid}", async () =>
            {
                FlowRule rule;

                using (var sp = _scopeFactor.CreateScope())
                {
                    using (var context = sp.ServiceProvider.GetRequiredService<ApplicationDbContext>())
                    {
                        rule = await context.FlowRules.FirstOrDefaultAsync(c => c.RuleId == ruleid);
                        _allFlows = await context.Flows.Where(c => c.FlowRule == rule && c.FlowStatus > 0).ToListAsync();
                        _logger.LogInformation($"读取规则链{rule?.Name}({ruleid}),子流程共计:{_allFlows.Count}");
                    }
                }
                return (rule, _allFlows);
            }, TimeSpan.FromSeconds(_setting.RuleCachingExpiration));
            if (cacheRule.HasValue)
            {
                _allFlows = cacheRule.Value._allFlows;
                var flow = _allFlows.FirstOrDefault(c => c.FlowId == flowId);
                var flows = _allFlows.Where(c => c.SourceId == flow.bpmnid).ToList();
                var emptyflow = flows.Where(c => c.Conditionexpression == string.Empty).ToList() ?? new List<Flow>();
                var tasks = new BaseRuleTask()
                {
                    Name = flow.Flowname,
                    Eventid = flow.bpmnid,
                    id = flow.bpmnid,
                    outgoing = new EditableList<BaseRuleFlow>()
                };
                foreach (var item in flows.Except(emptyflow))
                {
                    var rule = new BaseRuleFlow();
                    rule.id = item.bpmnid;
                    rule.Name = item.bpmnid;
                    rule.Eventid = item.bpmnid;
                    rule.Expression = item.Conditionexpression;
                    tasks.outgoing.Add(rule);
                }
                if (tasks.outgoing.Count > 0)
                {
                    SimpleFlowExcutor flowExcutor = new SimpleFlowExcutor();
                    var result = await flowExcutor.Excute(new FlowExcuteEntity()
                    {
                        Params = data,
                        Task = tasks,
                    });
                    var next = result.Where(c => c.IsSuccess).ToList();
                    foreach (var item in next)
                    {
                        var nextflow = flows.FirstOrDefault(a => a.bpmnid == item.Rule.SuccessEvent);
                        emptyflow.Add(nextflow);
                    }
                }
                return new ConditionTestResult { Failed = flows.Except(emptyflow).ToList(), Passed = emptyflow };
            }
            else
            {
                return null;
            }
        }
    }
}