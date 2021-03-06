﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Stateless.WorkflowEngine;
using Stateless.WorkflowEngine.Exceptions;
using Stateless.WorkflowEngine.Stores;
using NUnit.Framework;
using Test.Stateless.WorkflowEngine.Workflows.Basic;
using Test.Stateless.WorkflowEngine.Workflows.Broken;
using Test.Stateless.WorkflowEngine.Workflows.Delayed;
using Test.Stateless.WorkflowEngine.Workflows.SingleInstance;
using Stateless.WorkflowEngine.Models;
using Stateless.WorkflowEngine.Services;
using NSubstitute;
using Stateless.WorkflowEngine.Events;

namespace Test.Stateless.WorkflowEngine
{
    [TestFixture]
    public class WorkflowServerTest
    {

        #region ExecuteWorkflow Tests

        [Test]
        [ExpectedException(ExpectedException=typeof(NullReferenceException))]
        public void ExecuteWorkflow_NullWorkflow_RaisesException()
        {
            IWorkflowServer workflowServer = new WorkflowServer(Substitute.For<IWorkflowStore>());
            workflowServer.ExecuteWorkflow(null);
        }

        [Test]
        public void ExecuteWorkflow_OnExecution_InitialisesAndFiresTriggers()
        {
            // set up the store and the workflows
            IWorkflowStore workflowStore = new MemoryWorkflowStore();

            BasicWorkflow workflow = new BasicWorkflow(BasicWorkflow.State.Start);
            workflow.CreatedOn = DateTime.UtcNow.AddMinutes(-2);
            workflow.ResumeTrigger = BasicWorkflow.Trigger.DoStuff.ToString();
            workflowStore.Save(workflow);

            // execute
            IWorkflowServer workflowServer = new WorkflowServer(workflowStore);
            workflowServer.ExecuteWorkflow(workflow);

            Assert.AreEqual(BasicWorkflow.State.Complete.ToString(), workflow.CurrentState);

        }

        [Test]
        public void ExecuteWorkflow_OnSuccessfulExecution_RetryCountIsZero()
        {
            // set up the store and the workflows
            IWorkflowStore workflowStore = new MemoryWorkflowStore();

            BasicWorkflow workflow = new BasicWorkflow(BasicWorkflow.State.Start);
            workflow.CreatedOn = DateTime.UtcNow.AddMinutes(-2);
            workflow.ResumeTrigger = BasicWorkflow.Trigger.DoStuff.ToString();
            workflowStore.Save(workflow);

            // execute
            IWorkflowServer workflowEngine = new WorkflowServer(workflowStore);
            workflowEngine.ExecuteWorkflow(workflow);

            Assert.AreEqual(0, workflow.RetryCount);

        }

        [Test]
        public void ExecuteWorkflow_OnCompletion_MovesWorkflowIntoCompletedArchive()
        {
            // set up the store and the workflows
            IWorkflowStore workflowStore = new MemoryWorkflowStore();

            BasicWorkflow workflow = new BasicWorkflow(BasicWorkflow.State.DoingStuff);
            workflow.CreatedOn = DateTime.UtcNow.AddMinutes(-2);
            workflow.ResumeTrigger = BasicWorkflow.Trigger.Complete.ToString();
            workflowStore.Save(workflow);

            // execute
            IWorkflowServer workflowEngine = new WorkflowServer(workflowStore);
            workflowEngine.ExecuteWorkflow(workflow);

            Assert.IsNull(workflowStore.GetOrDefault(workflow.Id));
            Assert.IsNotNull(workflowStore.GetCompleted(workflow.Id));

        }

        [Test]
        public void ExecuteWorkflow_OnCompletion_WorkflowCompletedEventRaised()
        {
            // set up the store and the workflows
            IWorkflowStore workflowStore = new MemoryWorkflowStore();
            bool eventRaised = false;

            BasicWorkflow workflow = new BasicWorkflow(BasicWorkflow.State.DoingStuff);
            workflow.CreatedOn = DateTime.UtcNow.AddMinutes(-2);
            workflow.ResumeTrigger = BasicWorkflow.Trigger.Complete.ToString();
            workflowStore.Save(workflow);

            // execute
            IWorkflowServer workflowServer = new WorkflowServer(workflowStore);
            workflowServer.WorkflowCompleted += delegate(object sender, WorkflowEventArgs e)
            {
                eventRaised = true;
            };

            workflowServer.ExecuteWorkflow(workflow);

            Assert.IsTrue(eventRaised);
        }

        [Test]
        public void ExecuteWorkflow_OnCompletion_WorkflowOnCompleteCalled()
        {
            // set up the store and the workflows
            IWorkflowStore workflowStore = new MemoryWorkflowStore();

            BasicWorkflow workflow = Substitute.For<BasicWorkflow>(BasicWorkflow.State.DoingStuff);
            workflow.CreatedOn = DateTime.UtcNow.AddMinutes(-2);
            workflow.ResumeTrigger = BasicWorkflow.Trigger.Complete.ToString();
            workflowStore.Save(workflow);

            workflow.When(x => x.Fire("Complete")).Do(x => workflow.IsComplete = true);

            // execute
            IWorkflowServer workflowServer = new WorkflowServer(workflowStore);
            workflowServer.ExecuteWorkflow(workflow);

            workflow.Received(1).OnComplete();
        }

        [Test]
        public void ExecuteWorkflow_OnStepExceptionAndSingleInstanceWorkflow_CorrectMethodCalled()
        {
            Workflow workflow = Substitute.For<Workflow>();
            workflow.ResumeTrigger = "Test";
            workflow.IsSingleInstance = true;
            workflow.WhenForAnyArgs(x => x.Fire(Arg.Any<string>())).Do(x => { throw new Exception(); });

            IWorkflowExceptionHandler exceptionHandler = Substitute.For<IWorkflowExceptionHandler>();

            // execute
            IWorkflowServer workflowServer = new WorkflowServer(Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRegistrationService>(), exceptionHandler);
            workflowServer.ExecuteWorkflow(workflow);

            exceptionHandler.Received(1).HandleWorkflowException(Arg.Any<Workflow>(), Arg.Any<Exception>());
        }

        [Test]
        public void ExecuteWorkflow_OnStepException_StateReset()
        {
            string initialState = Guid.NewGuid().ToString();

            Workflow workflow = Substitute.For<Workflow>();
            workflow.ResumeTrigger = "Test";
            workflow.CurrentState.Returns(initialState);
            workflow.WhenForAnyArgs(x => x.Fire(Arg.Any<string>())).Do(x => { throw new Exception(); });

            // execute
            IWorkflowServer workflowServer = new WorkflowServer(Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRegistrationService>(), Substitute.For<IWorkflowExceptionHandler>());
            workflowServer.ExecuteWorkflow(workflow);

            // make sure the property was set
            workflow.Received(1).CurrentState = initialState;
        }

        [Test]
        public void ExecuteWorkflow_OnStepExceptionAndMultipleInstanceWorkflow_CorrectMethodCalled()
        {
            Workflow workflow = Substitute.For<Workflow>();
            workflow.ResumeTrigger = "Test";
            workflow.IsSingleInstance = false;
            workflow.WhenForAnyArgs(x => x.Fire(Arg.Any<string>())).Do(x => { throw new Exception(); });

            IWorkflowExceptionHandler exceptionHandler = Substitute.For<IWorkflowExceptionHandler>();

            // execute
            IWorkflowServer workflowServer = new WorkflowServer(Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRegistrationService>(), exceptionHandler);
            workflowServer.ExecuteWorkflow(workflow);

            exceptionHandler.Received(1).HandleWorkflowException(Arg.Any<Workflow>(), Arg.Any<Exception>());

        }

        [Test]
        public void ExecuteWorkflow_OnStepCompletion_ExecutesNextStep()
        {
            // set up the store and the workflows
            IWorkflowStore workflowStore = new MemoryWorkflowStore();

            BasicWorkflow workflow = new BasicWorkflow("Start");
            workflow.CreatedOn = DateTime.UtcNow.AddMinutes(-2);
            workflow.ResumeTrigger = BasicWorkflow.Trigger.DoStuff.ToString();
            workflowStore.Save(workflow);

            // execute
            IWorkflowServer workflowEngine = new WorkflowServer(workflowStore);
            workflowEngine.ExecuteWorkflow(workflow);

            Assert.AreEqual("Complete", workflow.CurrentState);

        }

        [Test]
        public void ExecuteWorkflow_OnWorkflowSuspension_WorkflowSuspendedEventRaised()
        {
            string initialState = Guid.NewGuid().ToString();
            bool eventRaised = false;

            Workflow workflow = Substitute.For<Workflow>();
            workflow.ResumeTrigger = "Test";
            workflow.RetryIntervals =  new int[] { };
            workflow.CurrentState.Returns(initialState);
            workflow.WhenForAnyArgs(x => x.Fire(Arg.Any<string>())).Do(x => { throw new Exception(); });

            // make sure the workflow is suspended
            IWorkflowExceptionHandler workflowExceptionHandler = Substitute.For<IWorkflowExceptionHandler>();
            workflowExceptionHandler.WhenForAnyArgs(x => x.HandleWorkflowException(Arg.Any<Workflow>(), Arg.Any<Exception>())).Do(x => { workflow.IsSuspended = true; });

            // execute
            IWorkflowServer workflowServer = new WorkflowServer(Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRegistrationService>(), workflowExceptionHandler);
            workflowServer.WorkflowSuspended += delegate(object sender, WorkflowEventArgs e) {
                eventRaised = true;
            };
            workflowServer.ExecuteWorkflow(workflow);

            Assert.IsTrue(eventRaised);
        }

        [Test]
        public void ExecuteWorkflow_OnWorkflowError_OnErrorCalled()
        {
            string initialState = Guid.NewGuid().ToString();

            Workflow workflow = Substitute.For<Workflow>();
            workflow.ResumeTrigger = "Test";
            workflow.RetryIntervals = new int[] { };
            workflow.CurrentState.Returns(initialState);
            workflow.WhenForAnyArgs(x => x.Fire(Arg.Any<string>())).Do(x => { throw new Exception(); });

            // execute
            IWorkflowServer workflowServer = new WorkflowServer(Substitute.For<IWorkflowStore>(), Substitute.For<IWorkflowRegistrationService>(), Substitute.For<IWorkflowExceptionHandler>());
            workflowServer.ExecuteWorkflow(workflow);

            workflow.Received(1).OnError(Arg.Any<Exception>());
        }

        #endregion

        #region ExecuteWorkflows Tests

        [Test]
        public void ExecuteWorkflows_OnDelayedAction_ResumesAfterDelay()
        {
            // set up the store and the workflows
            IWorkflowStore workflowStore = new MemoryWorkflowStore();

            DelayedWorkflow workflow = new DelayedWorkflow(DelayedWorkflow.State.Start);
            workflow.CreatedOn = DateTime.UtcNow.AddMinutes(-2);
            workflow.ResumeTrigger = DelayedWorkflow.Trigger.DoStuff.ToString();
            workflowStore.Save(workflow);

            IWorkflowServer workflowServer = new WorkflowServer(workflowStore);

            // execute
            workflowServer.ExecuteWorkflows(5);
            workflow = workflowStore.Get<DelayedWorkflow>(workflow.Id);
            Assert.AreEqual(DelayedWorkflow.State.DoingStuff.ToString(), workflow.CurrentState);

            // execute again - nothing should have changed
            workflowServer.ExecuteWorkflows(5);
            workflow = workflowStore.Get<DelayedWorkflow>(workflow.Id);
            Assert.AreEqual(DelayedWorkflow.State.DoingStuff.ToString(), workflow.CurrentState);

            // delay and run - should be now be complete
            Thread.Sleep(3100);
            workflowServer.ExecuteWorkflows(5);
            Assert.IsNull(workflowStore.GetOrDefault(workflow.Id));
            Assert.IsNotNull(workflowStore.GetCompletedOrDefault(workflow.Id));

        }

        [TestCase(0)]
        [TestCase(3)]
        [TestCase(5)]
        [TestCase(10)]
        [TestCase(50)]
        public void ExecuteWorkflows_OnExecution_ReturnsNumberOfWorkflowsExecuted(int activeWorkflowCount)
        {
            const int executeCount = 10;
            int expectedResult = Math.Min(executeCount, activeWorkflowCount);
            // set up the store and the workflows
            IWorkflowStore workflowStore = new MemoryWorkflowStore();

            for (int i = 0; i < activeWorkflowCount; i++)
            {
                BasicWorkflow workflow = new BasicWorkflow(BasicWorkflow.State.Start);
                workflow.CreatedOn = DateTime.UtcNow.AddMinutes(-2);
                workflow.ResumeOn = DateTime.UtcNow.AddMinutes(-2);
                workflow.ResumeTrigger = BasicWorkflow.Trigger.DoStuff.ToString();
                workflowStore.Save(workflow);
            }

            IWorkflowServer workflowServer = new WorkflowServer(workflowStore);

            // execute
            int result = workflowServer.ExecuteWorkflows(10);
            Assert.AreEqual(expectedResult, result);

        }

        #endregion

        #region IsSingleInstanceWorkflowRegistered Tests

        [Test]
        public void IsSingleInstanceWorkflowRegistered_OnExecute_UsesService()
        {
            // set up the store and the workflows
            IWorkflowStore workflowStore = Substitute.For<IWorkflowStore>();
            IWorkflowRegistrationService regService = Substitute.For<IWorkflowRegistrationService>();

            IWorkflowServer workflowServer = new WorkflowServer(workflowStore, regService, Substitute.For<IWorkflowExceptionHandler>());
            workflowServer.IsSingleInstanceWorkflowRegistered<BasicWorkflow>();
            regService.Received(1).IsSingleInstanceWorkflowRegistered<BasicWorkflow>(workflowStore);
        }

        #endregion


        #region OnWorkflowStateEntry Tests

        [Test]
        public void OnWorkflowStateEntry_OnStateChange_Persisted()
        {
            BasicWorkflow workflow = new BasicWorkflow(BasicWorkflow.State.Start);
            workflow.ResumeTrigger = BasicWorkflow.Trigger.DoStuff.ToString();

            // set up the workflow store
            IWorkflowStore workflowStore = Substitute.For<IWorkflowStore>();
            workflowStore.GetActive(Arg.Any<int>()).Returns(new[] { workflow });

            IWorkflowServer workflowEngine = new WorkflowServer(workflowStore);
            workflowEngine.ExecuteWorkflows(10);

            // We should have received TWO saves as the workflow moves between the states
            workflowStore.Received(2).Save(workflow);

        }
        #endregion

        #region RegisterWorkflow Tests

        [Test]
        public void RegisterWorkflow_OnRegister_UsesService()
        {
            // set up the store and the workflows
            IWorkflowStore workflowStore = Substitute.For<IWorkflowStore>();
            IWorkflowRegistrationService regService = Substitute.For<IWorkflowRegistrationService>();

            BasicWorkflow workflow = new BasicWorkflow(BasicWorkflow.State.Start);
            IWorkflowServer workflowServer = new WorkflowServer(workflowStore, regService, Substitute.For<IWorkflowExceptionHandler>());
            workflowServer.RegisterWorkflow(workflow);

            regService.Received(1).RegisterWorkflow(workflowStore, workflow);

        }

        #endregion


    }
}
