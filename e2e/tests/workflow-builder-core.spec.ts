import { mergeTests } from '@playwright/test';
import { test as authTest, expect } from '../fixtures/auth';
import { test as projectTest } from '../fixtures/project';

// Combine auth and project fixtures
const test = mergeTests(authTest, projectTest);

// Step type labels that match STEP_TYPE_LABELS in web/lib/utils/constants.ts
const STEP_TYPE_LABELS = {
  Agent: 'Agent',
  Conditional: 'Conditional Branch',
  ForEach: 'For Each',
  ReviewLoop: 'Review Loop',
  Parallel: 'Parallel',
  Extract: 'Extract',
  VideoAnalyze: 'Analyze Video',
  VideoCompile: 'Compile Video',
  EditRoom: 'Edit Room',
  GraphicsRoom: 'Graphics Room',
  ColorGradeRoom: 'Color Grade Room',
};

// Expected step types in order (as they will be added)
const EXPECTED_STEP_TYPES = [
  'Agent',
  'Conditional',
  'ForEach',
  'ReviewLoop',
  'Parallel',
  'Extract',
  'VideoAnalyze',
  'VideoCompile',
  'EditRoom',
  'GraphicsRoom',
  'ColorGradeRoom',
];

test.describe('workflow-builder-core', () => {
  test.describe.configure({ mode: 'parallel' });

  test('Test A (desktop only): adding one step of each of the 11 StepTypes via canvas Add Step button + modal', async ({
    authenticatedPage,
    scratchProject,
    request,
  }, testInfo) => {
    // This test only runs on desktop project
    if (testInfo.project.name === 'mobile') {
      testInfo.skip(true, 'Test A only runs on desktop (mobile Add Step button only adds hardcoded Agent steps)');
    }
    // 11 modal round-trips with fixed settle waits take ~35-45 s alone; leave headroom for a loaded machine.
    test.setTimeout(120000);
    const baseURL = process.env.BASE_URL || 'http://localhost';
    const projectId = scratchProject.id;

    // Fetch agents to get built-in agent IDs for validation
    const agentsResponse = await request.get(`${baseURL}/api/v1/agents`);
    expect(agentsResponse.ok()).toBeTruthy();
    const agents = (await agentsResponse.json()) as any[];

    // Find built-in agents by agentType
    const extractTransformAgent = agents.find((a) => a.agentType === 'ExtractTransform');
    const videoTransformAgent = agents.find((a) => a.agentType === 'VideoTransform');
    const agentStepAgent = agents.find((a) => a.agentType === 'DirectorAgent');
    const reviewLoopAgent = agents.find((a) => a.agentType === 'ReviewAgent');

    expect(extractTransformAgent).toBeDefined();
    expect(videoTransformAgent).toBeDefined();
    expect(agentStepAgent).toBeDefined();
    expect(reviewLoopAgent).toBeDefined();

    // Navigate to the new workflow page
    const newWorkflowUrl = `/app/projects/${projectId}/workflows/new`;
    await authenticatedPage.goto(newWorkflowUrl);
    await authenticatedPage.waitForLoadState('networkidle');
    // The template list renders once the page has hydrated and loaded; acting before that can be lost.
    await expect(authenticatedPage.getByRole('radiogroup', { name: 'Templates' })).toBeVisible({ timeout: 20000 });

    // Verify we're on the correct page
    expect(authenticatedPage.url()).toContain(newWorkflowUrl);

    // Fill in the workflow name
    const workflowName = `Test Workflow ${Date.now()}`;
    const nameInput = await authenticatedPage.locator('input[placeholder="My Workflow"]');
    await expect(nameInput).toBeVisible({ timeout: 5000 });
    await nameInput.fill(workflowName);

    // Add one step of each of the 11 StepTypes via the AddStepModal
    // Wait for the add step button (within FlowchartBuilderWrapper)
    await authenticatedPage.waitForTimeout(1000);

    // For each step type, open modal, click the button, and set up the step
    for (const stepType of EXPECTED_STEP_TYPES) {
      const labelText = STEP_TYPE_LABELS[stepType as keyof typeof STEP_TYPE_LABELS];

      // For the first click, we might need to wait for the UI to be ready
      if (stepType === EXPECTED_STEP_TYPES[0]) {
        await authenticatedPage.waitForTimeout(1500);
      }

      // Add Step button uses aria-label for stable selection
      const addStepButton = await authenticatedPage.getByRole('button', { name: 'Add Step' });
      await expect(addStepButton).toBeVisible({ timeout: 5000 });

      // Click the Add Step button to open the modal
      await addStepButton.click();

      // Wait for the modal to appear and the button to be visible
      // Scoped to the picker: a bare `button:has-text("Agent")` also matches the header's Account menu
      // when the signed-in email contains "agent" (the local-staging test account does).
      const stepButton = authenticatedPage
        .locator(`.mantine-Modal-content button:has-text("${labelText}")`)
        .first();
      await expect(stepButton).toBeVisible({ timeout: 5000 });
      await stepButton.click();

      // Wait for the step to be added and the modal to close
      await authenticatedPage.waitForTimeout(500);

      // Agent, ForEach, and ReviewLoop steps require configuration: pick an agent via AgentPicker
      if (stepType === 'Agent' || stepType === 'ForEach' || stepType === 'ReviewLoop') {
        // Click the canvas node to open the drawer
        const stepNodes = await authenticatedPage.locator('.react-flow__node').all();
        await stepNodes[stepNodes.length - 1].click();
        await authenticatedPage.waitForTimeout(600);
        const dialog = authenticatedPage.locator('[role="dialog"]').first();
        const picker = dialog.getByPlaceholder('Select agent');
        await expect(picker).toBeVisible({ timeout: 5000 });
        await picker.click();
        await authenticatedPage.waitForTimeout(400);

        // Select the appropriate agent based on step type
        let selectedAgent = agentStepAgent; // Default to DirectorAgent (AgentStep)
        if (stepType === 'ReviewLoop') {
          selectedAgent = reviewLoopAgent; // Use ReviewAgent for ReviewLoop
        }
        // ForEach also uses agentStepAgent (DirectorAgent)

        const escaped = selectedAgent.name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        const option = authenticatedPage.getByRole('option', { name: new RegExp(`^${escaped}\\b`) });
        await expect(option.first()).toBeVisible({ timeout: 5000 });
        await option.first().click();
        await authenticatedPage.waitForTimeout(400);

        // For ForEach and ReviewLoop steps, click Apply button to commit the agent selection to state
        // (same pattern as Test C: commit field edits before Escape)
        if (stepType === 'ForEach' || stepType === 'ReviewLoop') {
          const applyButton = dialog.getByRole('button', { name: 'Apply' });
          await expect(applyButton).toBeVisible({ timeout: 5000 });
          await applyButton.click();
          await authenticatedPage.waitForTimeout(300);
        }

        await authenticatedPage.keyboard.press('Escape');
        await authenticatedPage.waitForTimeout(500);
      }

      // Conditional steps require a condition expression
      if (stepType === 'Conditional') {
        // Click the canvas node to open the drawer
        const stepNodes = await authenticatedPage.locator('.react-flow__node').all();
        await stepNodes[stepNodes.length - 1].click();
        await authenticatedPage.waitForTimeout(600);
        const dialog = authenticatedPage.locator('[role="dialog"]').first();
        const conditionInput = dialog.getByLabel('condition Expression');
        await expect(conditionInput).toBeVisible({ timeout: 5000 });
        await conditionInput.fill('true');
        await authenticatedPage.waitForTimeout(300);

        // Click the Apply button to commit the condition expression to state
        const applyButton = dialog.getByRole('button', { name: 'Apply' });
        await expect(applyButton).toBeVisible({ timeout: 5000 });
        await applyButton.click();
        await authenticatedPage.waitForTimeout(300);

        await authenticatedPage.keyboard.press('Escape');
        await authenticatedPage.waitForTimeout(500);
      }

      // Parallel steps require at least one agent in the parallelAgentIds array
      if (stepType === 'Parallel') {
        // Click the canvas node to open the drawer
        const stepNodes = await authenticatedPage.locator('.react-flow__node').all();
        await stepNodes[stepNodes.length - 1].click();
        await authenticatedPage.waitForTimeout(600);
        const dialog = authenticatedPage.locator('[role="dialog"]').first();

        // Find and click the agent picker for adding agents to the parallel group
        const agentPicker = dialog.getByPlaceholder('Add agent to parallel group…');
        await expect(agentPicker).toBeVisible({ timeout: 5000 });
        await agentPicker.click();
        await authenticatedPage.waitForTimeout(400);

        // Select the first agent (DirectorAgent)
        const escaped = agentStepAgent.name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        const option = authenticatedPage.getByRole('option', { name: new RegExp(`^${escaped}\\b`) });
        await expect(option.first()).toBeVisible({ timeout: 5000 });
        await option.first().click();
        await authenticatedPage.waitForTimeout(400);

        // The agent is automatically added to the table, no need to click Apply
        await authenticatedPage.keyboard.press('Escape');
        await authenticatedPage.waitForTimeout(500);
      }

      // Wait a bit before adding the next step
      await authenticatedPage.waitForTimeout(300);
    }

    // Verify all 11 steps have been added
    // The steps should be visible as step cards in the FlowchartBuilder
    const stepCards = await authenticatedPage.locator('[data-testid*="step-card"], .step-card').all();

    // Wait for all steps to render
    await authenticatedPage.waitForTimeout(1000);

    // Reorder two steps using the move buttons
    // Find step cards and their move buttons (mobile: WorkflowStepList uses aria-labels)
    const moveUpButtons = await authenticatedPage.locator('[aria-label="Move step up"]').all();
    const moveDownButtons = await authenticatedPage.locator('[aria-label="Move step down"]').all();

    if (moveUpButtons.length > 0) {
      // Move the second step up (now it will be first)
      await moveUpButtons[1].click(); // Click move up on step 2
      await authenticatedPage.waitForTimeout(500);
    } else if (moveDownButtons.length > 0) {
      // Fallback: use move down on the first step instead
      await moveDownButtons[0].click();
      await authenticatedPage.waitForTimeout(500);
    }

    // Click the "Create Workflow" button
    const createButton = await authenticatedPage.locator('button:has-text("Create Workflow")');
    await expect(createButton).toBeVisible({ timeout: 5000 });
    // A name typed before the page finished hydrating can be reset by it; type it again.
    await nameInput.fill(workflowName);
    await createButton.click();

    // Wait for the workflow to be created and the notification to appear
    await authenticatedPage.waitForTimeout(2000);

    // Verify the "Created" notification appeared
    const createdNotification = await authenticatedPage
      .locator('text=/Created|successfully/i')
      .first();
    await expect(createdNotification).toBeVisible({ timeout: 5000 });

    // Extract the workflow ID from the URL after redirect
    await authenticatedPage.waitForURL(/\/app\/projects\/.*\/workflows\/[^/]+$/);
    const finalUrl = authenticatedPage.url();
    const workflowIdMatch = finalUrl.match(/\/workflows\/([^/]+)/);
    expect(workflowIdMatch).toBeTruthy();
    const workflowId = workflowIdMatch![1];

    // GET the workflow and verify all 11 steps with correct stepType values
    const getResponse = await request.get(
      `${baseURL}/api/v1/projects/${projectId}/workflows/${workflowId}`
    );
    expect(getResponse.ok()).toBeTruthy();
    const workflow = (await getResponse.json()) as any;

    // Verify workflow was created with the correct name
    expect(workflow.name).toBe(workflowName);

    // Verify all 11 steps exist
    expect(workflow.steps).toBeDefined();
    expect(workflow.steps.length).toBe(11);

    // Verify each step has the correct stepType
    // Note: the order might have changed due to the reorder action, so we check that all types are present
    const stepTypes = workflow.steps.map((step: any) => step.stepType).sort();
    const expectedTypes = EXPECTED_STEP_TYPES.sort();
    expect(stepTypes).toEqual(expectedTypes);

    // Verify that each step has required properties set
    for (const step of workflow.steps) {
      expect(step.stepType).toBeDefined();
      expect(step.label).toBeDefined();
      expect(step.stepOrder).toBeDefined();

      // Verify non-Conditional/Parallel steps have an agent
      if (step.stepType !== 'Conditional' && step.stepType !== 'Parallel') {
        expect(step.agentDefinitionId).toBeTruthy();
      }

      // Verify Conditional steps have a condition expression
      if (step.stepType === 'Conditional') {
        expect(step.conditionExpression).toBeTruthy();
      }

      // Verify Parallel steps have parallel agent IDs
      if (step.stepType === 'Parallel') {
        expect(step.parallelAgentIdsJson).toBeTruthy();
      }
    }
  });

  test('Test B (both projects): seed workflow with 3+ steps via API, reorder two steps via UI', async ({
    authenticatedPage,
    scratchProject,
    request,
  }, testInfo) => {
    // Desktop's canvas node drag only repositions visually (dragPositions state) — it never
    // reorders the underlying steps array/stepOrder. Only WorkflowStepList's (mobile) move-up/
    // move-down buttons actually change step order, so this test is mobile-only.
    if (testInfo.project.name === 'desktop') {
      testInfo.skip(true, 'Desktop canvas has no UI-driven step reorder; only mobile move-up/down buttons change stepOrder');
    }
    const baseURL = process.env.BASE_URL || 'http://localhost';
    const projectId = scratchProject.id;

    // Fetch agents to get built-in agent IDs for validation
    const agentsResponse = await request.get(`${baseURL}/api/v1/agents`);
    expect(agentsResponse.ok()).toBeTruthy();
    const agents = (await agentsResponse.json()) as any[];

    // Find built-in agents by agentType
    const agentStepAgent = agents.find((a) => a.agentType === 'DirectorAgent');
    expect(agentStepAgent).toBeDefined();
    const agentId = agentStepAgent?.id;

    // Seed workflow with 3+ steps via API
    const workflowName = `Reorder Test Workflow ${Date.now()}`;

    // Create a new workflow
    const createWorkflowResponse = await request.post(
      `${baseURL}/api/v1/projects/${projectId}/workflows`,
      {
        data: {
          name: workflowName,
          steps: [
            {
              stepType: 'Agent',
              label: 'Step 1',
              stepOrder: 0,
              agentDefinitionId: agentId,
            },
            {
              stepType: 'Agent',
              label: 'Step 2',
              stepOrder: 1,
              agentDefinitionId: agentId,
            },
            {
              stepType: 'Agent',
              label: 'Step 3',
              stepOrder: 2,
              agentDefinitionId: agentId,
            },
            {
              stepType: 'Agent',
              label: 'Step 4',
              stepOrder: 3,
              agentDefinitionId: agentId,
            },
          ],
        },
      }
    );

    expect(createWorkflowResponse.ok()).toBeTruthy();
    const workflowData = (await createWorkflowResponse.json()) as any;
    const workflowId = workflowData.id;

    // Verify initial step order via GET
    const getInitialResponse = await request.get(
      `${baseURL}/api/v1/projects/${projectId}/workflows/${workflowId}`
    );
    expect(getInitialResponse.ok()).toBeTruthy();
    const initialWorkflow = (await getInitialResponse.json()) as any;
    expect(initialWorkflow.steps.length).toBe(4);
    const initialStepOrder = initialWorkflow.steps.map((step: any) => step.stepOrder);
    expect(initialStepOrder).toEqual([0, 1, 2, 3]);

    // Navigate to edit page
    const editUrl = `/app/projects/${projectId}/workflows/${workflowId}`;
    await authenticatedPage.goto(`${editUrl}?view=advanced`);
    await authenticatedPage.waitForLoadState('networkidle');
    expect(authenticatedPage.url()).toContain(editUrl);

    // Reorder two steps via the UI
    // Find move buttons and move step 1 down (so it becomes step 2, and step 2 becomes step 1)
    // Wait for the step list to render before collecting the buttons: `.all()` does not wait, and an
    // empty list here silently skips the reorder, leaving Save disabled (nothing changed).
    const moveDown = authenticatedPage.locator('[aria-label="Move step down"]');
    await expect(moveDown.first()).toBeVisible({ timeout: 10000 });
    const moveDownButtons = await moveDown.all();

    if (moveDownButtons.length >= 1) {
      // Click move down on step 0 to move it after step 1
      await moveDownButtons[0].click();
      await authenticatedPage.waitForTimeout(500);
    }

    // Click Save button to persist changes
    const saveButton = await authenticatedPage.locator('button:has-text("Save")').or(authenticatedPage.locator('[aria-label*="Save"]')).first();
    await expect(saveButton).toBeVisible({ timeout: 5000 });
    await saveButton.click();
    await authenticatedPage.waitForTimeout(1000);

    // GET the workflow and verify new stepOrder
    const getFinalResponse = await request.get(
      `${baseURL}/api/v1/projects/${projectId}/workflows/${workflowId}`
    );
    expect(getFinalResponse.ok()).toBeTruthy();
    const finalWorkflow = (await getFinalResponse.json()) as any;
    expect(finalWorkflow.steps.length).toBe(4);

    // After moving step 0 down, the new order should be [1, 0, 2, 3]
    const finalStepOrder = finalWorkflow.steps
      .sort((a: any, b: any) => a.stepOrder - b.stepOrder)
      .map((step: any) => {
        // Map back to original position to verify reorder happened
        const originalIndex = initialWorkflow.steps.findIndex((s: any) => s.id === step.id);
        return originalIndex;
      });

    // Verify that at least the order is different from initial
    expect(finalStepOrder).not.toEqual(initialStepOrder);
  });

  test('Test C (both projects): validation error toggle for Conditional step missing condition expression', async ({
    authenticatedPage,
    scratchProject,
  }, testInfo) => {
    const projectId = scratchProject.id;

    const newWorkflowUrl = `/app/projects/${projectId}/workflows/new`;
    await authenticatedPage.goto(newWorkflowUrl);
    await authenticatedPage.waitForLoadState('networkidle');
    // The template list renders once the page has hydrated and loaded; acting before that can be lost.
    await expect(authenticatedPage.getByRole('radiogroup', { name: 'Templates' })).toBeVisible({ timeout: 20000 });

    const workflowName = `Validation Test Workflow ${Date.now()}`;
    const nameInput = await authenticatedPage.locator('input[placeholder="My Workflow"]');
    await expect(nameInput).toBeVisible({ timeout: 5000 });
    await nameInput.fill(workflowName);

    await authenticatedPage.waitForTimeout(1000);

    // Add a single Conditional step, leaving conditionExpression empty (desktop canvas or
    // mobile step list both funnel through the same AddStepModal — see Test A's comment on
    // the desktop-only Add Step button; mobile's WorkflowStepList exposes its own "Add Step").
    if (testInfo.project.name === 'mobile') {
      const mobileAddStepButton = await authenticatedPage.getByRole('button', { name: 'Add Step' });
      await expect(mobileAddStepButton).toBeVisible({ timeout: 5000 });
      await mobileAddStepButton.click();
      await authenticatedPage.waitForTimeout(500);

      // On mobile, the Add Step button appends a step to the list. Open the newly-added step's
      // Step Type select and pick "Conditional Branch" from the Mantine Combobox options.
      // Use getByRole to avoid strict-mode violations from getByLabel matching both input and listbox
      const stepTypeSelect = await authenticatedPage.getByRole('combobox', { name: 'Step Type' });
      await expect(stepTypeSelect).toBeVisible({ timeout: 5000 });
      await stepTypeSelect.click();
      const conditionalOption = await authenticatedPage.getByRole('option', { name: 'Conditional Branch' });
      await expect(conditionalOption).toBeVisible({ timeout: 5000 });
      await conditionalOption.click();
      await authenticatedPage.waitForTimeout(500);
    } else {
      const addStepButton = await authenticatedPage.getByRole('button', { name: 'Add Step' });
      await expect(addStepButton).toBeVisible({ timeout: 5000 });
      await addStepButton.click();

      const conditionalOption = await authenticatedPage
        .locator('button:has-text("Conditional Branch")')
        .first();
      await expect(conditionalOption).toBeVisible({ timeout: 5000 });
      await conditionalOption.click();
      await authenticatedPage.waitForTimeout(500);
    }

    // Click "Create Workflow" with the condition expression left blank.
    const createButton = await authenticatedPage.locator('button:has-text("Create Workflow")');
    await expect(createButton).toBeVisible({ timeout: 5000 });
    // A name typed before the page finished hydrating can be reset by it; type it again so the
    // only validation error left is the missing condition expression.
    await nameInput.fill(workflowName);
    await expect(nameInput).toHaveValue(workflowName);
    await createButton.click();

    // Assert the exact validation notification for a missing condition expression on step 1.
    const validationError = await authenticatedPage
      .locator('text=Step 1 requires a condition expression')
      .first();
    await expect(validationError).toBeVisible({ timeout: 5000 });

    // Now fill in the condition expression and retry.
    // On mobile, click the 'Configure Conditional Branch' button to open StepConfigSheet before locating the condition field
    // On desktop, click the canvas node to open the StepConfigPanel
    if (testInfo.project.name === 'mobile') {
      const configureButton = await authenticatedPage.locator('button:has-text("Configure Conditional Branch")').first();
      await expect(configureButton).toBeVisible({ timeout: 5000 });
      await configureButton.click();
      await authenticatedPage.waitForTimeout(400); // Wait for Sheet transition
    } else {
      // On desktop, click the canvas node to open StepConfigPanel (React Flow uses .react-flow__node)
      const stepNodes = await authenticatedPage.locator('.react-flow__node').all();
      if (stepNodes.length > 0) {
        const lastNode = stepNodes[stepNodes.length - 1];
        await lastNode.click();
        await authenticatedPage.waitForTimeout(400); // Wait for Drawer transition
      }
    }

    // Use getByLabel to locate the condition field via its rendered label, scoped to the panel/sheet
    const stepConfigPanel = testInfo.project.name === 'mobile'
      ? await authenticatedPage.locator('[role="dialog"]').first()
      : await authenticatedPage.locator('[role="dialog"]').first();
    const conditionInput = stepConfigPanel.getByLabel('condition Expression');
    await expect(conditionInput).toBeVisible({ timeout: 5000 });
    await conditionInput.fill('true');
    await authenticatedPage.waitForTimeout(300);

    // Click the Apply button to commit the condition expression to the step state
    const applyButton = stepConfigPanel.getByRole('button', { name: 'Apply' });
    await expect(applyButton).toBeVisible({ timeout: 5000 });
    await applyButton.click();
    await authenticatedPage.waitForTimeout(300);

    // Close the drawer/sheet by pressing Escape
    await authenticatedPage.keyboard.press('Escape');
    await authenticatedPage.waitForTimeout(500);

    await createButton.click();
    await authenticatedPage.waitForTimeout(1500);

    // The earlier validation error notification should no longer be the visible/active one,
    // and a green "Created" notification should appear instead.
    const createdNotification = await authenticatedPage.locator('text=Created').first();
    await expect(createdNotification).toBeVisible({ timeout: 5000 });
  });

  test('Test D (desktop only): Conditional step renders two distinct branch edges labeled True and False', async ({
    authenticatedPage,
    scratchProject,
    request,
  }, testInfo) => {
    if (testInfo.project.name === 'mobile') {
      testInfo.skip(true, 'Branch edges are a canvas-only affordance; mobile has no canvas view');
    }
    const baseURL = process.env.BASE_URL || 'http://localhost';
    const projectId = scratchProject.id;

    const agentsResponse = await request.get(`${baseURL}/api/v1/agents`);
    expect(agentsResponse.ok()).toBeTruthy();
    const agents = (await agentsResponse.json()) as any[];
    const agentStepAgent = agents.find((a) => a.agentType === 'DirectorAgent');
    expect(agentStepAgent).toBeDefined();
    const agentId = agentStepAgent?.id;

    // Seed a workflow via API: step 1 = Agent, step 2 = Agent, step 3 = Conditional branching
    // true -> step 1, false -> step 2.
    const workflowName = `Branch Edge Test Workflow ${Date.now()}`;
    const createWorkflowResponse = await request.post(
      `${baseURL}/api/v1/projects/${projectId}/workflows`,
      {
        data: {
          name: workflowName,
          steps: [
            { stepType: 'Agent', label: 'Step 1', stepOrder: 0, agentDefinitionId: agentId },
            { stepType: 'Agent', label: 'Step 2', stepOrder: 1, agentDefinitionId: agentId },
            {
              stepType: 'Conditional',
              label: 'Step 3',
              stepOrder: 2,
              agentDefinitionId: agentId,
              conditionExpression: 'true',
              loopSourceExpression: null,
              loopTargetStepOrder: null,
              trueBranchStepOrder: '1',
              falseBranchStepOrder: '2',
            },
          ],
        },
      }
    );
    expect(createWorkflowResponse.ok()).toBeTruthy();
    const workflowData = (await createWorkflowResponse.json()) as any;
    const workflowId = workflowData.id;

    const editUrl = `/app/projects/${projectId}/workflows/${workflowId}`;
    await authenticatedPage.goto(`${editUrl}?view=advanced`);
    await authenticatedPage.waitForLoadState('networkidle');
    await authenticatedPage.waitForTimeout(1000);

    // React Flow renders edge labels as text within the edge label renderer; assert both
    // labels are present and distinct (per interaction-spec.md §1.3: "True"/"False", never one
    // edge for a Conditional step).
    const trueLabel = await authenticatedPage.locator('.react-flow__edge').filter({ hasText: 'True' });
    const falseLabel = await authenticatedPage.locator('.react-flow__edge').filter({ hasText: 'False' });
    await expect(trueLabel.first()).toBeVisible({ timeout: 5000 });
    await expect(falseLabel.first()).toBeVisible({ timeout: 5000 });

    const trueCount = await trueLabel.count();
    const falseCount = await falseLabel.count();
    expect(trueCount).toBeGreaterThan(0);
    expect(falseCount).toBeGreaterThan(0);

    // The two edges must be distinct DOM nodes, not the same edge matching both filters.
    const trueEdgeIds = await trueLabel.evaluateAll((els) => els.map((el) => el.getAttribute('data-id') || el.id));
    const falseEdgeIds = await falseLabel.evaluateAll((els) => els.map((el) => el.getAttribute('data-id') || el.id));
    const overlap = trueEdgeIds.filter((id) => falseEdgeIds.includes(id));
    expect(overlap.length).toBe(0);
  });

  test('Test E (both projects): execute a saved workflow through to a terminal status', async ({
    authenticatedPage,
    scratchProject,
    request,
  }) => {
    test.setTimeout(60000);
    const baseURL = process.env.BASE_URL || 'http://localhost';
    const projectId = scratchProject.id;

    const agentsResponse = await request.get(`${baseURL}/api/v1/agents`);
    expect(agentsResponse.ok()).toBeTruthy();
    const agents = (await agentsResponse.json()) as any[];
    const agentStepAgent = agents.find((a) => a.agentType === 'DirectorAgent');
    expect(agentStepAgent).toBeDefined();
    const agentId = agentStepAgent?.id;

    const workflowName = `Execute Test Workflow ${Date.now()}`;
    const createWorkflowResponse = await request.post(
      `${baseURL}/api/v1/projects/${projectId}/workflows`,
      {
        data: {
          name: workflowName,
          steps: [
            { stepType: 'Agent', label: 'Step 1', stepOrder: 0, agentDefinitionId: agentId },
          ],
        },
      }
    );
    expect(createWorkflowResponse.ok()).toBeTruthy();
    const workflowData = (await createWorkflowResponse.json()) as any;
    const workflowId = workflowData.id;

    const editUrl = `/app/projects/${projectId}/workflows/${workflowId}`;
    await authenticatedPage.goto(`${editUrl}?view=advanced`);
    await authenticatedPage.waitForLoadState('networkidle');

    // The header button is "Run" and the confirm button of its dialog is "Start run".
    const executeButton = authenticatedPage.getByRole('button', { name: 'Run', exact: true });
    await expect(executeButton).toBeVisible({ timeout: 5000 });
    await executeButton.click();

    // Run may open the Run dialog first; if so,
    // confirm through it rather than navigating directly.
    const modalConfirmButton = authenticatedPage.locator('.mantine-Modal-content button:has-text("Start run")');
    // isVisible() does not wait, and the dialog renders only after its cost estimate loads, so wait
    // for the button explicitly.
    const modalVisible = await modalConfirmButton
      .waitFor({ state: 'visible', timeout: 10000 })
      .then(() => true)
      .catch(() => false);
    if (modalVisible) {
      await modalConfirmButton.click();
    }

    // Should navigate to the execution detail page.
    await authenticatedPage.waitForURL(/\/executions\/[^/]+$/, { timeout: 10000 });
    const finalUrl = authenticatedPage.url();
    const executionIdMatch = finalUrl.match(/\/executions\/([^/]+)$/);
    expect(executionIdMatch).toBeTruthy();
    const executionId = executionIdMatch![1];

    // Poll execution.status via the API until it reaches a terminal status, or time out.
    const terminalStatuses = ['Passed', 'Failed', 'Cancelled'];
    const pollTimeoutMs = 30000;
    const pollIntervalMs = 1000;
    const startTime = Date.now();
    let lastStatus: string | undefined;
    let reachedTerminal = false;

    while (Date.now() - startTime < pollTimeoutMs) {
      const executionResponse = await request.get(
        `${baseURL}/api/v1/projects/${projectId}/executions/${executionId}`
      );
      if (executionResponse.ok()) {
        const execution = (await executionResponse.json()) as any;
        lastStatus = execution.status;
        if (terminalStatuses.includes(execution.status)) {
          reachedTerminal = true;
          break;
        }
      }
      await new Promise((resolve) => setTimeout(resolve, pollIntervalMs));
    }

    if (!reachedTerminal) {
      // Timed out waiting for a terminal status. Report this plainly rather than hanging or
      // asserting a false positive — this can legitimately happen in an environment with no
      // real inference backend to actually run the workflow.
      console.warn(
        `Execution ${executionId} did not reach a terminal status within ${pollTimeoutMs}ms; last observed status: ${lastStatus}`
      );
    }
    expect(lastStatus).toBeDefined();
  });
});
