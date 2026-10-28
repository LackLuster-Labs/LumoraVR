#version 450
layout(local_size_x = 64) in;
struct Point { vec4 pos; vec4 prev; vec4 goal; int pin; float distance; int first; int count; };
struct Edge { int a; int b; float rest; float ka; float kb; float r0; float r1; float r2; };
layout(set=0,binding=0,std430) buffer Points { Point p[]; };
layout(set=0,binding=1,std430) readonly buffer Edges { Edge e[]; };
layout(set=0,binding=2,std430) readonly buffer Triangles { int tri[]; };
layout(set=0,binding=3,std430) readonly buffer Adjacent { int adj[]; };
layout(set=0,binding=4,std430) buffer Wind { vec4 wind[]; };
layout(push_constant,std430) uniform Params { vec4 pass; vec4 motion; vec4 gravity; vec4 air; } pc;
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(pc.pass.z)) return;
    int stage = int(pc.pass.x);
    if (stage == 0) {
        vec3 force = vec3(0);
        if (dot(pc.air.xyz,pc.air.xyz) > 1e-8 && p[i].goal.w > 1e-8) {
            for (int j=0;j<p[i].count;j++) {
                int t=adj[p[i].first+j]*3;
                Point a=p[tri[t]],b=p[tri[t+1]],c=p[tri[t+2]];
                vec3 areaNormal=cross(b.pos.xyz-a.pos.xyz,c.pos.xyz-a.pos.xyz)*0.5;
                float area=length(areaNormal);
                if(area<1e-8) continue;
                vec3 n=areaNormal/area;
                vec3 velocity=pc.gravity.w>0.0 ? (a.pos.xyz+b.pos.xyz+c.pos.xyz-a.prev.xyz-b.prev.xyz-c.prev.xyz)/(3.0*pc.motion.x) : vec3(0);
                force+=n*dot(n,pc.air.xyz-velocity)*(area/3.0);
            }
            force/=p[i].goal.w;
        }
        wind[i]=vec4(force,0);
    } else if(stage==1) {
        vec3 pos=p[i].pos.xyz;
        if(p[i].pos.w>0.0) { p[i].pos.xyz=p[i].goal.xyz; p[i].prev.xyz=p[i].goal.xyz; return; }
        vec3 velocity=pc.gravity.w>0.0 ? (pos-p[i].prev.xyz)*(pc.motion.y*pc.motion.z) : vec3(0);
        float speed=length(velocity);
        if(speed>pc.motion.w) velocity*=pc.motion.w/speed;
        p[i].prev.xyz=pos;
        vec3 next=pos+velocity+(pc.gravity.xyz+wind[i].xyz)*(pc.pass.w*pc.pass.w);
        p[i].pos.xyz=any(isnan(next))||any(isinf(next)) ? pos : next;
    } else if(stage==2) {
        Edge edge=e[i+uint(pc.pass.y)];
        vec3 delta=p[edge.b].pos.xyz-p[edge.a].pos.xyz;
        float distance=length(delta);
        if(distance<1e-6) return;
        vec3 correction=delta*((distance-edge.rest)/distance*pc.air.w);
        if(edge.ka>0.0) p[edge.a].pos.xyz+=correction*edge.ka;
        if(edge.kb>0.0) p[edge.b].pos.xyz-=correction*edge.kb;
    } else {
        if(p[i].pos.w>0.0) return;
        vec3 pos=p[i].pos.xyz;
        if(p[i].pin>=0) {
            vec3 anchor=p[p[i].pin].pos.xyz;
            vec3 delta=pos-anchor;
            float distance=length(delta);
            if(distance>p[i].distance && distance>1e-6) pos=anchor+delta*(p[i].distance/distance);
        }
        p[i].pos.xyz=mix(pos,p[i].goal.xyz,p[i].prev.w);
    }
}
