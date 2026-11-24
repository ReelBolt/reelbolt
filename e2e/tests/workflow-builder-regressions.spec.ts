import { mergeTests } from '@playwright/test';
import { test as authTest, expect } from '../fixtures/auth';
import { test as projectTest } from '../fixtures/project';

// Combine auth and project fixtures
const test = mergeTests(authTest, projectTest);

test.describe('workflow-builder-regressions', () => {
  test('Spec 1: dragging canvas connection should be a no-op and not mutate step order/branch fields', async ({
    authenticatedPage,
    scratchProject,
    request,
  }, testInfo) => {
    // Spec 1 is desktop-canvas-only (React Flow connection-handle dragging)
    if (testInfo.project.name === 'mobile') {
      testInfo.skip(true, 'Spec 1 only runs on desktop (React Flow canvas not present on mobile)');
    }

    const baseURL = process.env.BASE_URL || 'http://localhost';
    const projectId = scratchProject.id;

    // Fetch the ExtractTransform agent ID
    const agentsResponse = await request.get(`${baseURL}/api/v1/agents`);
    expect(agentsResponse.ok()).toBeTruthy();
    const agents = (await agentsResponse.json()) as any[];
    const extractTransformAgent = agents.find((a) => a.agentType === 'ExtractTransform');
    expect(extractTransformAgent).toBeDefined();
    const agentId = extractTransformAgent!.id;

    // Create a workflow with 2 Extract steps
    const createWorkflowResponse = await request.post(`${baseURL}/api/v1/projects/${projectId}/workflows`, {
      data: {
        name: 'Test Canvas Connection Workflow',
        steps: [
          {
            agentDefinitionId: agentId,
            stepOrder: 1,
            label: 'Extract Step 1',
            stepType: 'Extract',
            conditionExpression: null,
            loopSourceExpression: null,
            loopTargetStepOrder: null,
            trueBranchStepOrder: null,
            falseBranchStepOrder: null,
            extractConfigJson: JSON.stringify({
              model: 'gpt-4-vision',
              view: 'Previous',
            }),
          },
          {
            agentDefinitionId: agentId,
            stepOrder: 2,
            label: 'Extract Step 2',
            stepType: 'Extract',
            conditionExpression: null,
            loopSourceExpression: null,
            loopTargetStepOrder: null,
            trueBranchStepOrder: null,
            falseBranchStepOrder: null,
            extractConfigJson: JSON.stringify({
              model: 'gpt-4-vision',
              view: 'Previous',
            }),
          },
        ],
      },
    });

    expect(createWorkflowResponse.ok()).toBeTruthy();
    const workflow = (await createWorkflowResponse.json()) as any;
    const workflowId = workflow.id;

    // Fetch initial workflow state to capture original step order/branch values
    const initialGetResponse = await request.get(
      `${baseURL}/api/v1/projects/${projectId}/workflows/${workflowId}`
    );
    expect(initialGetResponse.ok()).toBeTruthy();
    const initialWorkflow = (await initialGetResponse.json()) as any;
    const step0Initial = initialWorkflow.steps[0];
    const step1Initial = initialWorkflow.steps[1];
    const step0InitialId = step0Initial.id;
    const step1InitialId = step1Initial.id;

    // Navigate to the workflow edit page
    const workflowEditUrl = `/app/projects/${projectId}/workflows/${workflowId}`;
    await authenticatedPage.goto(`${workflowEditUrl}?view=advanced`);
    await authenticatedPage.waitForLoadState('networkidle');

    // Verify we're on the correct page
    expect(authenticatedPage.url()).toContain(workflowEditUrl);

    // Wait for the FlowchartBuilder to load by waiting for the canvas/flow div to be visible
    await authenticatedPage.waitForSelector('[data-testid="reactflow-wrapper"]', { timeout: 5000 }).catch(() => {
      // If the test ID doesn't exist, that's ok - we'll try other selectors
    });

    // Wait a bit more for the UI to fully initialize
    await authenticatedPage.waitForTimeout(2000);

    // Try to find and drag a connection handle
    // React Flow renders handles with .react-flow__handle class
    // Look for the first node and try to find its output handle
    const nodes = await authenticatedPage.locator('[data-testid*="node"]').all();
    expect(nodes.length).toBeGreaterThanOrEqual(2);

    // React Flow nodes contain .react-flow__handle elements for connections
    const node0Handles = await nodes[0].locator('.react-flow__handle').all();
    const node1Handles = await nodes[1].locator('.react-flow__handle').all();
    expect(node0Handles.length).toBeGreaterThan(0);
    expect(node1Handles.length).toBeGreaterThan(0);

    // Get the bounding box of the output handle of first node
    const handle0Box = await node0Handles[0].boundingBox();
    const handle1Box = await node1Handles[0].boundingBox();
    expect(handle0Box).toBeTruthy();
    expect(handle1Box).toBeTruthy();

    if (handle0Box && handle1Box) {
      // Simulate dragging from first node's handle to second node's handle
      // Add intermediate moves to ensure drag threshold is crossed
      const startX = handle0Box.x + handle0Box.width / 2;
      const startY = handle0Box.y + handle0Box.height / 2;
      const endX = handle1Box.x + handle1Box.width / 2;
      const endY = handle1Box.y + handle1Box.height / 2;

      await authenticatedPage.mouse.move(startX, startY);
      await authenticatedPage.mouse.down();
      // Add intermediate moves
      await authenticatedPage.mouse.move(startX + (endX - startX) * 0.25, startY + (endY - startY) * 0.25);
      await authenticatedPage.mouse.move(startX + (endX - startX) * 0.5, startY + (endY - startY) * 0.5);
      await authenticatedPage.mouse.move(startX + (endX - startX) * 0.75, startY + (endY - startY) * 0.75);
      await authenticatedPage.mouse.move(endX, endY);
      await authenticatedPage.mouse.up();

      // Wait for any potential state changes
      await authenticatedPage.waitForTimeout(1000);
    }

    // The drag must not have changed the workflow, so there is nothing to save: Save Changes only
    // enables once the form is dirty.
    const saveButton = authenticatedPage.locator('button:has-text("Save Changes")');
    await expect(saveButton).toBeVisible();
    await expect(saveButton).toBeDisabled();

    // Fetch the workflow after the drag
    const finalGetResponse = await request.get(
      `${baseURL}/api/v1/projects/${projectId}/workflows/${workflowId}`
    );
    expect(finalGetResponse.ok()).toBeTruthy();
    const finalWorkflow = (await finalGetResponse.json()) as any;
    // Match steps by label, not id (ids are never stable across a save -- see Context, not a product bug)
    const step0Final = finalWorkflow.steps.find((s: any) => s.label === step0Initial.label);
    const step1Final = finalWorkflow.steps.find((s: any) => s.label === step1Initial.label);
    expect(step0Final).toBeDefined();
    expect(step1Final).toBeDefined();

    // Assert that step order and branch fields are unchanged
    // The onConnect is now a no-op, so these fields should be the same
    expect(step0Final.stepOrder).toBe(step0Initial.stepOrder);
    expect(step1Final.stepOrder).toBe(step1Initial.stepOrder);

    // Check branch fields (they should be null/unchanged from the initial state)
    expect(step0Final.trueBranchStepOrder).toBe(step0Initial.trueBranchStepOrder);
    expect(step0Final.falseBranchStepOrder).toBe(step0Initial.falseBranchStepOrder);
    expect(step1Final.trueBranchStepOrder).toBe(step1Initial.trueBranchStepOrder);
    expect(step1Final.falseBranchStepOrder).toBe(step1Initial.falseBranchStepOrder);
  });

  test('Spec 2: unknown JSON keys in step config should be preserved through edit and save', async ({
    authenticatedPage,
    scratchProject,
    request,
  }) => {
    const baseURL = process.env.BASE_URL || 'http://localhost';
    const projectId = scratchProject.id;

    // Fetch the ExtractTransform agent ID
    const agentsResponse = await request.get(`${baseURL}/api/v1/agents`);
    expect(agentsResponse.ok()).toBeTruthy();
    const agents = (await agentsResponse.json()) as any[];
    const extractTransformAgent = agents.find((a) => a.agentType === 'ExtractTransform');
    expect(extractTransformAgent).toBeDefined();
    const agentId = extractTransformAgent!.id;

    // Create a workflow with an Extract step containing an unknown JSON key
    const unknownKeyValue = 'preserved-value-' + Date.now();
    const createWorkflowResponse = await request.post(`${baseURL}/api/v1/projects/${projectId}/workflows`, {
      data: {
        name: 'Test Unknown Field Preservation',
        steps: [
          {
            agentDefinitionId: agentId,
            stepOrder: 0,
            label: 'Extract Step with Unknown Key',
            stepType: 'Extract',
            extractConfigJson: JSON.stringify({
              model: 'gpt-4-vision',
              view: 'Previous',
              unknownCustomField: unknownKeyValue,
            }),
          },
        ],
      },
    });

    expect(createWorkflowResponse.ok()).toBeTruthy();
    const workflow = (await createWorkflowResponse.json()) as any;
    const workflowId = workflow.id;

    // Verify the unknown key is in the initial response
    const initialGetResponse = await request.get(
      `${baseURL}/api/v1/projects/${projectId}/workflows/${workflowId}`
    );
    expect(initialGetResponse.ok()).toBeTruthy();
    const initialWorkflow = (await initialGetResponse.json()) as any;
    const initialConfig = JSON.parse(initialWorkflow.steps[0].extractConfigJson);
    expect(initialConfig.unknownCustomField).toBe(unknownKeyValue);

    // Navigate to the workflow edit page
    const workflowEditUrl = `/app/projects/${projectId}/workflows/${workflowId}`;
    await authenticatedPage.goto(`${workflowEditUrl}?view=advanced`);
    await authenticatedPage.waitForLoadState('networkidle');

    // Verify we're on the correct page
    expect(authenticatedPage.url()).toContain(workflowEditUrl);

    // Wait for the page to fully load
    await authenticatedPage.waitForTimeout(2000);

    // Edit an unrelated field (the step label) via the UI
    // The step's name field: on the canvas a node's "Step 1 name" input (placeholder "Name this step
    // ..."), on the phone's step list the "Step label" input.
    const labelInput = authenticatedPage
      .locator('input[placeholder^="Name this step"], input[placeholder="Step label"]')
      .first();
    await expect(labelInput).toBeVisible({ timeout: 3000 });
    await labelInput.click();
    await labelInput.clear();
    await labelInput.fill('Updated Label ' + Date.now());

    // Click Save Changes button
    const saveButton = await authenticatedPage.locator('button:has-text("Save Changes")');
    await expect(saveButton).toBeVisible({ timeout: 3000 });
    await saveButton.click();
    // Wait for save to complete
    await authenticatedPage.waitForTimeout(2000);

    // Fetch the workflow after the edit and save
    const finalGetResponse = await request.get(
      `${baseURL}/api/v1/projects/${projectId}/workflows/${workflowId}`
    );
    expect(finalGetResponse.ok()).toBeTruthy();
    const finalWorkflow = (await finalGetResponse.json()) as any;
    const finalConfig = JSON.parse(finalWorkflow.steps[0].extractConfigJson);

    // Assert that the unknown key survived the round-trip
    expect(finalConfig.unknownCustomField).toBe(unknownKeyValue);
  });

  test('Spec 3: dragged canvas node should maintain its position after unrelated step-config edit and save (desktop only)', async ({
    authenticatedPage,
    scratchProject,
    request,
  }) => {
    // Skip on mobile — no canvas to drag
    // Check if viewport is mobile size (based on playwright.config.ts: mobile is 390x844)
    const viewportSize = authenticatedPage.viewportSize();
    if (viewportSize && viewportSize.width < 600) {
      return; // Skip on mobile viewport
    }

    const baseURL = process.env.BASE_URL || 'http://localhost';
    const projectId = scratchProject.id;

    // Fetch an agent to use for the workflow
    const agentsResponse = await request.get(`${baseURL}/api/v1/agents`);
    expect(agentsResponse.ok()).toBeTruthy();
    const agents = (await agentsResponse.json()) as any[];
    // Find an agent with agentType === 'DirectorAgent' or use the first entry
    const agent = agents.find((a) => a.agentType === 'DirectorAgent') || agents[0];
    expect(agent).toBeDefined();
    const agentDefinitionId = agent!.id;

    // Create a workflow with 2 Agent steps
    const createWorkflowResponse = await request.post(`${baseURL}/api/v1/projects/${projectId}/workflows`, {
      data: {
        name: 'Test Drag Position Persistence',
        steps: [
          {
            stepType: 'Agent',
            label: 'Step 1',
            stepOrder: 0,
            agentDefinitionId,
          },
          {
            stepType: 'Agent',
            label: 'Step 2',
            stepOrder: 1,
            agentDefinitionId,
          },
        ],
      },
    });

    expect(createWorkflowResponse.ok()).toBeTruthy();
    const workflow = (await createWorkflowResponse.json()) as any;
    const workflowId = workflow.id;

    // Navigate to the workflow edit page
    const workflowEditUrl = `/app/projects/${projectId}/workflows/${workflowId}`;
    await authenticatedPage.goto(`${workflowEditUrl}?view=advanced`);
    await authenticatedPage.waitForLoadState('networkidle');

    // Verify we're on the correct page
    expect(authenticatedPage.url()).toContain(workflowEditUrl);

    // Wait for the FlowchartBuilder to load
    await authenticatedPage.waitForSelector('[data-testid="reactflow-wrapper"]', { timeout: 5000 }).catch(() => {
      // If the test ID doesn't exist, that's ok
    });

    // Wait a bit more for the UI to fully initialize
    await authenticatedPage.waitForTimeout(2000);

    // Get all canvas nodes
    const nodes = await authenticatedPage.locator('.react-flow__node').all();
    expect(nodes.length).toBeGreaterThanOrEqual(2);

    // Read the initial transform of the first node
    const node0 = nodes[0];
    const initialTransform = await node0.evaluate((el) => {
      return el.style.transform;
    });
    expect(initialTransform).toBeDefined();
    expect(initialTransform).not.toBe('');

    // Parse initial position from transform
    // Browsers may normalize translate() to matrix(); try both formats
    let initialMatch = initialTransform!.match(/translate\(([^,]+),\s*([^)]+)\)/);
    let initialX: number;
    let initialY: number;
    if (initialMatch) {
      initialX = parseFloat(initialMatch[1]);
      initialY = parseFloat(initialMatch[2]);
    } else {
      // Parse matrix(a, b, c, d, tx, ty) format
      const matrixMatch = initialTransform!.match(/matrix\(([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^)]+)\)/);
      expect(matrixMatch).toBeTruthy();
      initialX = parseFloat(matrixMatch![5]);
      initialY = parseFloat(matrixMatch![6]);
    }

    // Drag the first node to a new position
    const node0Box = await node0.boundingBox();
    expect(node0Box).toBeTruthy();

    if (node0Box) {
      const startX = node0Box.x + node0Box.width / 2;
      const startY = node0Box.y + node0Box.height / 2;
      const dragOffsetX = 100;
      const dragOffsetY = 50;

      // Perform drag with intermediate mouse.move() calls so React Flow's drag threshold fires
      await authenticatedPage.mouse.move(startX, startY);
      await authenticatedPage.mouse.down();
      // Add several intermediate moves to ensure drag threshold is crossed
      await authenticatedPage.mouse.move(startX + dragOffsetX * 0.25, startY + dragOffsetY * 0.25);
      await authenticatedPage.mouse.move(startX + dragOffsetX * 0.5, startY + dragOffsetY * 0.5);
      await authenticatedPage.mouse.move(startX + dragOffsetX * 0.75, startY + dragOffsetY * 0.75);
      await authenticatedPage.mouse.move(startX + dragOffsetX, startY + dragOffsetY);
      await authenticatedPage.mouse.up();

      // Wait for drag to settle
      await authenticatedPage.waitForTimeout(500);
    }

    // Read the dragged transform (should be different)
    const draggedTransform = await node0.evaluate((el) => {
      return el.style.transform;
    });
    let draggedMatch = draggedTransform!.match(/translate\(([^,]+),\s*([^)]+)\)/);
    let draggedX: number;
    let draggedY: number;
    if (draggedMatch) {
      draggedX = parseFloat(draggedMatch[1]);
      draggedY = parseFloat(draggedMatch[2]);
    } else {
      // Parse matrix(a, b, c, d, tx, ty) format
      const matrixMatch = draggedTransform!.match(/matrix\(([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^)]+)\)/);
      expect(matrixMatch).toBeTruthy();
      draggedX = parseFloat(matrixMatch![5]);
      draggedY = parseFloat(matrixMatch![6]);
    }

    // Verify the drag actually moved the node
    expect(Math.abs(draggedX - initialX) + Math.abs(draggedY - initialY)).toBeGreaterThan(0);

    // Click on a DIFFERENT node (the second one) to open its config drawer
    const node1 = nodes[1];
    await node1.click();

    // Wait for the drawer to open
    await authenticatedPage.waitForTimeout(500);

    // Find and edit the 'Step label' input
    // The Agent step's label input is inside its own fieldset with placeholder "Label for this step"
    const labelInput = await authenticatedPage.locator('input[placeholder="Label for this step"]').first();
    await expect(labelInput).toBeVisible({ timeout: 3000 });
    await labelInput.click();
    await labelInput.clear();
    await labelInput.fill('Updated Step Label ' + Date.now());

    // Close the drawer by pressing Escape before clicking Save Changes
    await authenticatedPage.keyboard.press('Escape');
    await authenticatedPage.waitForTimeout(500);

    // Click the "Save Changes" button
    const saveButton = await authenticatedPage.locator('button:has-text("Save Changes")');
    await expect(saveButton).toBeVisible({ timeout: 3000 });
    await saveButton.click();

    // Wait for save to complete
    await authenticatedPage.waitForTimeout(2000);

    // Re-read the dragged node's transform and verify it's unchanged (or within 2px tolerance)
    const finalTransform = await node0.evaluate((el) => {
      const style = window.getComputedStyle(el);
      return style.transform;
    });
    let finalMatch = finalTransform!.match(/translate\(([^,]+),\s*([^)]+)\)/);
    let finalX: number;
    let finalY: number;
    if (finalMatch) {
      finalX = parseFloat(finalMatch[1]);
      finalY = parseFloat(finalMatch[2]);
    } else {
      // Parse matrix(a, b, c, d, tx, ty) format
      const matrixMatch = finalTransform!.match(/matrix\(([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^)]+)\)/);
      expect(matrixMatch).toBeTruthy();
      finalX = parseFloat(matrixMatch![5]);
      finalY = parseFloat(matrixMatch![6]);
    }

    // Assert the position is unchanged (within 2px tolerance)
    const xDifference = Math.abs(finalX - draggedX);
    const yDifference = Math.abs(finalY - draggedY);
    expect(xDifference).toBeLessThanOrEqual(2);
    expect(yDifference).toBeLessThanOrEqual(2);
  });
});
