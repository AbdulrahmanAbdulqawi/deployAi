import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DeployPlanComponent } from './deploy-plan.component';
import { DeploymentPlan, DeploymentPlanKind } from '../../core/models/api.models';

describe('DeployPlanComponent', () => {
  let fixture: ComponentFixture<DeployPlanComponent>;

  const plan: DeploymentPlan = {
    parts: [{ role: 'website', providerName: 'vercel' }],
    confidence: 'high',
    plainSummary: 'Looks like a website. We will put it on fast global hosting.'
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeployPlanComponent]
    }).compileComponents();

    fixture = TestBed.createComponent(DeployPlanComponent);
    fixture.componentRef.setInput('plan', plan);
    fixture.componentRef.setInput('activeParts', plan.parts);
    fixture.detectChanges();
  });

  it('shows the plain-language summary', () => {
    expect(fixture.nativeElement.textContent).toContain(plan.plainSummary);
  });

  it('shows Coolify full-stack branding when planKind is coolify-fullstack', () => {
    const fullStackPlan: DeploymentPlan = {
      parts: [
        { role: 'website', providerName: 'coolify' },
        { role: 'server', providerName: 'coolify' }
      ],
      confidence: 'high',
      plainSummary: 'Deploy everything to Coolify.',
      planKind: DeploymentPlanKind.CoolifyFullStack
    };

    fixture.componentRef.setInput('plan', fullStackPlan);
    fixture.componentRef.setInput('activeParts', fullStackPlan.parts);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Coolify full-stack setup');
    expect(fixture.nativeElement.textContent).toContain('Coolify · Static site on Coolify');
  });

  it('describes services rather than separate apps when planKind is coolify-compose', () => {
    const composePlan: DeploymentPlan = {
      parts: [
        { role: 'website', providerName: 'coolify' },
        { role: 'server', providerName: 'coolify' }
      ],
      confidence: 'high',
      plainSummary: 'Deploy everything to Coolify.',
      planKind: DeploymentPlanKind.CoolifyCompose
    };

    fixture.componentRef.setInput('plan', composePlan);
    fixture.componentRef.setInput('activeParts', composePlan.parts);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Single-origin Coolify setup');
    expect(text).toContain('served from a single domain');
    // "Static site on Coolify" implies a second URL that a compose deployment does not have.
    expect(text).not.toContain('Static site on Coolify');
  });

  /**
   * The destination picker can offer "create a new project". Offering it without asking for the
   * name meant the recommended path submitted nameless and the provider refused. The name field
   * exists only while that option is selected, so an existing project never shows it.
   */
  it('asks for a name only when the new-destination option is selected', () => {
    fixture.componentRef.setInput('destinations', [
      { id: 'proj-a', name: 'alpha' },
      { id: 'proj-b', name: 'beta' },
      { id: '__new__', name: 'New Coolify project…' }
    ]);
    fixture.componentRef.setInput('newDestinationId', '__new__');
    fixture.componentRef.setInput('selectedDestinationId', 'proj-a');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('New project name');

    fixture.componentRef.setInput('selectedDestinationId', '__new__');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('New project name');
  });

  /**
   * The card that refuses the deploy is where the offer to fix it belongs. It used to say "use
   * Manual override to set up these files, then come back" — a screen that reconfigures which
   * parts get deployed and never writes a file, for files DeployAI generates itself. A
   * non-technical user reads "manual override" as "you do this part by hand".
   */
  it('offers to write the missing files rather than sending the user to manual override', () => {
    const composePlan: DeploymentPlan = {
      parts: [
        { role: 'website', providerName: 'coolify' },
        { role: 'server', providerName: 'coolify' }
      ],
      confidence: 'high',
      plainSummary: 'Deploy everything to Coolify.',
      planKind: DeploymentPlanKind.CoolifyCompose
    };

    fixture.componentRef.setInput('plan', composePlan);
    fixture.componentRef.setInput('activeParts', composePlan.parts);
    fixture.componentRef.setInput('readiness', {
      isReady: false,
      usesSplitOrigin: false,
      usesSingleOriginCompose: true,
      warnings: [],
      missingFiles: [
        { path: 'docker-compose.coolify.yml', reason: 'A Docker Compose file is required.', severity: 'blocking' }
      ]
    });
    fixture.componentRef.setInput('canSetUpFiles', true);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('DeployAI can write these for you');
    expect(text).not.toContain('to set up these files, then come back');
  });

  it('reports the typed project name to its parent', () => {
    fixture.componentRef.setInput('destinations', [
      { id: 'proj-a', name: 'alpha' },
      { id: '__new__', name: 'New Coolify project…' }
    ]);
    fixture.componentRef.setInput('newDestinationId', '__new__');
    fixture.componentRef.setInput('selectedDestinationId', '__new__');
    fixture.detectChanges();

    const emitted: string[] = [];
    fixture.componentInstance.newDestinationNameChange.subscribe(name => emitted.push(name));
    const input: HTMLInputElement = fixture.nativeElement.querySelector('input[placeholder="my-app"]');
    input.value = 'deployai-shapes';
    input.dispatchEvent(new Event('input'));

    expect(emitted).toEqual(['deployai-shapes']);
  });
});
