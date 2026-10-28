#version 450
layout(local_size_x=64) in;
struct Body { vec4 pos; vec4 rot; vec4 linear; vec4 angular; vec4 extent; vec4 inertia; vec4 force; vec4 torque; vec4 moveMask; vec4 turnMask; vec4 contact; vec4 previous; };
layout(set=0,binding=0,std430) readonly buffer Input { Body body[]; };
layout(set=0,binding=1,std430) writeonly buffer Output { Body result[]; };
layout(set=0,binding=2,std430) coherent buffer Heads { int head[]; };
layout(set=0,binding=3,std430) coherent buffer Next { int next[]; };
layout(set=0,binding=4,std430) readonly buffer Large { int large[]; };
layout(push_constant,std430) uniform Params { vec4 pass; vec4 step; vec4 gravity; } pc;
vec3 rotate(vec4 q,vec3 v) { return v+2.0*cross(q.xyz,cross(q.xyz,v)+q.w*v); }
vec3 unrotate(vec4 q,vec3 v) { return rotate(vec4(-q.xyz,q.w),v); }
vec3 inertia(Body b,vec3 v) { return rotate(b.rot,unrotate(b.rot,v)*b.inertia.xyz); }
ivec3 cellOf(vec3 p) { return ivec3(floor(p/pc.step.y)); }
uint hashCell(ivec3 cell) { return (uint(cell.x)*73856093u ^ uint(cell.y)*19349663u ^ uint(cell.z)*83492791u)&(uint(pc.pass.z)-1u); }
vec3 capped(vec3 v,float limit) { float speed=length(v); return speed>limit ? v*(limit/speed) : v; }
mat3 axes(Body b) { return mat3(rotate(b.rot,vec3(1,0,0)),rotate(b.rot,vec3(0,1,0)),rotate(b.rot,vec3(0,0,1))); }
vec3 support(Body b,mat3 r,vec3 n,vec3 target) {
    vec3 p=b.pos.xyz;
    for(int k=0;k<3;k++) {
        float along=dot(r[k],n);
        p+=r[k]*(abs(along)*b.extent[k]>0.0025 ? sign(along)*b.extent[k] : clamp(dot(target-b.pos.xyz,r[k]),-b.extent[k],b.extent[k]));
    }
    return p;
}
bool axisTest(vec3 axis,mat3 ar,vec3 ah,mat3 br,vec3 bh,vec3 delta,inout float depth,inout vec3 normal) {
    float len=length(axis);
    if(len<1e-5) return true;
    vec3 n=axis/len;
    float ra=dot(abs(transpose(ar)*n),ah), rb=dot(abs(transpose(br)*n),bh);
    float overlap=ra+rb-abs(dot(delta,n));
    if(overlap<0.0) return false;
    if(overlap<depth) { depth=overlap; normal=dot(delta,n)>=0.0 ? n : -n; }
    return true;
}
bool sphereBox(Body sphere,Body box,out vec3 n,out float depth,out vec3 point) {
    vec3 local=unrotate(box.rot,sphere.pos.xyz-box.pos.xyz);
    vec3 nearest=clamp(local,-box.extent.xyz,box.extent.xyz);
    vec3 delta=nearest-local;
    float distance=length(delta);
    if(distance>sphere.extent.x) return false;
    if(distance>1e-6) { n=rotate(box.rot,delta/distance); depth=sphere.extent.x-distance; }
    else {
        vec3 gap=box.extent.xyz-abs(local);
        int axis=gap.x<gap.y ? (gap.x<gap.z ? 0 : 2) : (gap.y<gap.z ? 1 : 2);
        vec3 outward=vec3(0); outward[axis]=local[axis]>=0.0 ? 1.0 : -1.0;
        n=-rotate(box.rot,outward); depth=sphere.extent.x+gap[axis];
        nearest=local+outward*gap[axis];
    }
    point=box.pos.xyz+rotate(box.rot,nearest);
    return true;
}
bool contact(Body a,Body b,out vec3 n,out float depth,out vec3 point) {
    vec3 delta=b.pos.xyz-a.pos.xyz;
    float reach=length(a.extent.xyz)+length(b.extent.xyz);
    if(dot(delta,delta)>reach*reach) return false;
    if(a.extent.w>0.0 && b.extent.w>0.0) {
        float distance=length(delta);
        depth=a.extent.x+b.extent.x-distance;
        if(depth<0.0) return false;
        n=distance>1e-6 ? delta/distance : vec3(0,1,0);
        point=(a.pos.xyz+n*a.extent.x+b.pos.xyz-n*b.extent.x)*0.5;
        return true;
    }
    if(a.extent.w>0.0) return sphereBox(a,b,n,depth,point);
    if(b.extent.w>0.0) { bool hit=sphereBox(b,a,n,depth,point); n=-n; return hit; }
    mat3 ar=axes(a),br=axes(b);
    depth=1e30; n=vec3(0,1,0);
    for(int k=0;k<3;k++) {
        if(!axisTest(ar[k],ar,a.extent.xyz,br,b.extent.xyz,delta,depth,n)) return false;
        if(!axisTest(br[k],ar,a.extent.xyz,br,b.extent.xyz,delta,depth,n)) return false;
    }
    for(int k=0;k<3;k++) for(int j=0;j<3;j++)
        if(!axisTest(cross(ar[k],br[j]),ar,a.extent.xyz,br,b.extent.xyz,delta,depth,n)) return false;
    vec3 tangent=normalize(cross(n,abs(n.x)<0.7 ? vec3(1,0,0) : vec3(0,1,0)));
    vec3 bitangent=cross(n,tangent);
    float an=dot(abs(transpose(ar)*n),a.extent.xyz);
    float bn=dot(abs(transpose(br)*n),b.extent.xyz);
    point=n*((dot(a.pos.xyz,n)+an+dot(b.pos.xyz,n)-bn)*0.5);
    for(int k=0;k<2;k++) {
        vec3 axis=k==0 ? tangent : bitangent;
        float ah=dot(abs(transpose(ar)*axis),a.extent.xyz);
        float bh=dot(abs(transpose(br)*axis),b.extent.xyz);
        float ac=dot(a.pos.xyz,axis),bc=dot(b.pos.xyz,axis);
        float low=max(ac-ah,bc-bh),high=min(ac+ah,bc+bh);
        point+=axis*((low+high)*0.5);
    }
    vec3 pa=support(a,ar,n,point),pb=support(b,br,-n,pa);
    for(int k=0;k<3;k++) { pa=support(a,ar,n,pb); pb=support(b,br,-n,pa); }
    point=(pa+pb)*0.5;
    return true;
}
void collide(uint i,int j,Body a,inout vec3 position,inout vec3 velocity,inout vec3 spin,inout float count,inout vec3 normals) {
    if(int(i)==j) return;
    Body b=body[j];
    vec3 n,point; float depth;
    if(!contact(a,b,n,depth,point)) return;
    float mass=a.pos.w+b.pos.w;
    if(mass<1e-9) return;
    position-=n*(max(0.0,depth-0.0005)*a.pos.w/mass*0.65);
    count+=1.0; normals-=n;
    vec3 ra=point-a.pos.xyz,rb=point-b.pos.xyz;
    vec3 relative=a.linear.xyz+cross(a.angular.xyz,ra)-b.linear.xyz-cross(b.angular.xyz,rb);
    float closing=dot(relative,n);
    float denom=mass+dot(cross(ra,n),inertia(a,cross(ra,n)))+dot(cross(rb,n),inertia(b,cross(rb,n)));
    float bounce=pc.step.z>0.0 && closing>0.5 ? min(1.0,a.torque.w+b.torque.w) : 0.0;
    float jn=max(0.0,closing)*(1.0+bounce)/max(denom,1e-9);
    vec3 impulse=n*jn;
    vec3 tangent=relative-n*closing;
    float speed=length(tangent);
    if(speed>1e-6) {
        tangent/=speed;
        float td=mass+dot(cross(ra,tangent),inertia(a,cross(ra,tangent)))+dot(cross(rb,tangent),inertia(b,cross(rb,tangent)));
        impulse+=tangent*min(speed/max(td,1e-9),min(a.inertia.w,b.inertia.w)*jn);
    }
    velocity-=impulse*a.pos.w;
    spin-=inertia(a,cross(ra,impulse));
}
void main() {
    uint i=gl_GlobalInvocationID.x;
    int stage=int(pc.pass.x);
    if(stage==1) { if(i<uint(pc.pass.z)) head[i]=-1; return; }
    if(i>=uint(pc.pass.y)) return;
    Body a=body[i];
    if(stage==0) {
        a.previous=a.pos;
        if(a.pos.w>0.0) {
            float dt=pc.step.x;
            a.linear.xyz=capped((a.linear.xyz+(pc.gravity.xyz*a.force.w+a.force.xyz*a.pos.w*pc.gravity.w)*dt)*max(0.0,1.0-a.linear.w*dt),8.0);
            a.angular.xyz=capped((a.angular.xyz+inertia(a,a.torque.xyz)*dt*pc.gravity.w)*max(0.0,1.0-a.angular.w*dt),16.0);
            a.pos.xyz+=a.linear.xyz*dt;
            vec3 omega=a.angular.xyz;
            a.rot=normalize(a.rot+vec4(omega*a.rot.w+cross(omega,a.rot.xyz),-dot(omega,a.rot.xyz))*(0.5*dt));
        }
        a.contact=vec4(0);
        result[i]=a;
    } else if(stage==2) {
        next[i]=-1;
        if(a.moveMask.w==0.0) next[i]=atomicExchange(head[hashCell(cellOf(a.pos.xyz))],int(i));
    } else if(stage==4) {
        vec3 movement=(a.pos.xyz-a.previous.xyz)/pc.step.x;
        bool still=a.contact.w>0.0 && dot(movement,movement)<0.0004 && dot(a.angular.xyz,a.angular.xyz)<0.0004;
        a.turnMask.w=still ? a.turnMask.w+pc.step.x : 0.0;
        result[i]=a;
    } else {
        if(a.pos.w<=0.0) { result[i]=a; return; }
        vec3 position=vec3(0),velocity=vec3(0),spin=vec3(0),normals=vec3(0); float count=0.0;
        ivec3 center=cellOf(a.pos.xyz);
        for(int z=-1;z<=1;z++) for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++) {
            ivec3 cell=center+ivec3(x,y,z);
            int j=head[hashCell(cell)];
            // A corrupt chain must fail the step instead of keeping a dispatch running. -xlinka
            int visited=0;
            while(j>=0 && j<int(pc.pass.y) && visited<int(pc.pass.y)) {
                if(all(equal(cellOf(body[j].pos.xyz),cell))) collide(i,j,a,position,velocity,spin,count,normals);
                j=next[j];
                visited++;
            }
            if(j!=-1) atomicOr(head[int(pc.pass.z)],1);
        }
        for(int j=0;j<int(pc.pass.w);j++) collide(i,large[j],a,position,velocity,spin,count,normals);
        float weight=1.0/max(1.0,count);
        a.pos.xyz+=position*weight;
        a.linear.xyz=capped(a.linear.xyz+velocity*weight,8.0);
        a.angular.xyz=capped(a.angular.xyz+spin*weight,16.0);
        if(count>0.0) a.contact=vec4(normals*weight,count);
        result[i]=a;
    }
}
