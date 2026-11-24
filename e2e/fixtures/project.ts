import { test as base, expect, APIRequestContext } from '@playwright/test';
import { test as authTest } from './auth';

export interface TestProject {
  id: string;
  name: string;
}

export const test = authTest.extend<{ scratchProject: TestProject, request: APIRequestContext }>({
  request: async ({ authenticatedPage }, use) => {
    // Use the authenticated page's request context so API calls include auth cookies
    await use(authenticatedPage.request);
  },
  scratchProject: async ({ request }, use) => {
    const baseURL = process.env.BASE_URL || 'http://localhost';
    let projectId: string = '';

    // Create a scratch project before the test
    const createResponse = await request.post(`${baseURL}/api/v1/projects`, {
      data: {
        name: `Test Project ${Date.now()}`,
        description: 'Temporary test project for e2e testing',
      },
    });

    expect(createResponse.ok()).toBeTruthy();
    const projectData = await createResponse.json() as any;
    projectId = projectData.id;

    const project: TestProject = {
      id: projectId,
      name: projectData.name,
    };

    await use(project);

    // Teardown: delete the scratch project after the test
    if (projectId) {
      const deleteResponse = await request.delete(`${baseURL}/api/v1/projects/${projectId}`);
      // Don't fail the test if deletion fails, but log it
      if (!deleteResponse.ok()) {
        console.warn(`Failed to delete scratch project ${projectId}`);
      }
    }
  },
});

export { expect };
