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
});
