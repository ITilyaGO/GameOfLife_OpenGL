#version 330 core
out vec4 FragColor;

in vec2 vUV;

uniform sampler2D currentState;

void main()
{
    float c = texture(currentState, vUV).r;
    FragColor = vec4(vec3(c), 1.0);
}
